using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Fleans.Streaming.Kafka;

/// <summary>Why a message was routed to the dead-letter topic; written to the <c>x-fleans-failure-reason</c> header.</summary>
internal static class KafkaDeadLetterReason
{
    public const string DeliveryFailed = "delivery-failed";
}

/// <summary>
/// Publishes the original raw Kafka record to <c>&lt;topic&gt;&lt;DeadLetterTopicSuffix&gt;</c> with diagnostic headers.
/// One instance per adapter factory, shared by every queue's delivery-failure handler.
/// </summary>
internal sealed partial class KafkaDeadLetterPublisher : IDisposable
{
    private readonly KafkaStreamingOptions _options;
    private readonly ILogger<KafkaDeadLetterPublisher> _logger;
    // Bounds how long a DLQ publish can hold up the pulling agent awaiting OnDeliveryFailure
    // (librdkafka's default message.timeout.ms is 5 minutes).
    internal const int MessageTimeoutMs = 30_000;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

    private readonly IProducer<byte[], byte[]> _producer;
    private int _disposed;
    private int _producerDisposed;
    private int _inFlight;

    public KafkaDeadLetterPublisher(KafkaStreamingOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<KafkaDeadLetterPublisher>();

        var config = KafkaQueueAdapter.BuildProducerConfig(options);
        config.MessageTimeoutMs = MessageTimeoutMs;
        var producerBuilder = new ProducerBuilder<byte[], byte[]>(config);
        if (options.OAuthBearerTokenProvider is not null)
            producerBuilder.SetOAuthBearerTokenRefreshHandler(options.OAuthBearerTokenProvider);
        _producer = producerBuilder.Build();
    }

    /// <summary>
    /// Holds off <see cref="Dispose"/> (bounded) until released, so a caller can span work that ends in
    /// a publish — e.g. re-consume then publish. <c>null</c> once disposed.
    /// </summary>
    public IDisposable? TryAcquire()
    {
        // Increment before the disposed check: Dispose sets the flag first and then waits for
        // _inFlight to drain, so a lease taken before the flag finishes on an open producer.
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _disposed) == 0) return new Lease(this);
        Release();
        return null;
    }

    private void Release()
    {
        // Dispose gave up draining — the last lease out releases the producer.
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposed) != 0)
            DisposeProducer();
    }

    /// <summary>Never throws — a failed DLQ publish is logged (EventId 12001) and the record is dropped.</summary>
    public async Task PublishAsync(
        string sourceTopic, int partition, long offset, byte[]? key, byte[] value,
        string reason, string? streamId = null)
    {
        using var lease = TryAcquire();
        if (lease is null)
        {
            LogDroppedAfterDispose(reason, sourceTopic, partition, offset);
            return;
        }

        await ProduceAsync(sourceTopic, partition, offset, key, value, reason, streamId).ConfigureAwait(false);
    }

    /// <summary>
    /// Publish for a caller that already holds a lease from <see cref="TryAcquire"/>; skips the
    /// disposed check, because the caller's lease is what keeps the producer open.
    /// </summary>
    public Task PublishUnderLeaseAsync(
        string sourceTopic, int partition, long offset, byte[]? key, byte[] value,
        string reason, string? streamId, string? subscriptionId) =>
        ProduceAsync(sourceTopic, partition, offset, key, value, reason, streamId, subscriptionId);

    private sealed class Lease(KafkaDeadLetterPublisher owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release();
        }
    }

    private async Task ProduceAsync(
        string sourceTopic, int partition, long offset, byte[]? key, byte[] value, string reason, string? streamId,
        string? subscriptionId = null)
    {
        var dlqTopic = KafkaTopicNaming.DeadLetterTopicForSource(_options, sourceTopic);
        var headers = new Headers
        {
            { "x-fleans-original-topic", Utf8(sourceTopic) },
            { "x-fleans-original-partition", Utf8(partition.ToString(CultureInfo.InvariantCulture)) },
            { "x-fleans-original-offset", Utf8(offset.ToString(CultureInfo.InvariantCulture)) },
            { "x-fleans-failure-reason", Utf8(reason) },
            { "x-fleans-failure-time-utc", Utf8(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)) },
        };
        if (streamId is not null)
            headers.Add("x-fleans-stream-id", Utf8(streamId));
        if (subscriptionId is not null)
            headers.Add("x-fleans-subscription-id", Utf8(subscriptionId));

        try
        {
            var dr = await _producer.ProduceAsync(dlqTopic, new Message<byte[], byte[]>
            {
                Key = key!,
                Value = value,
                Headers = headers,
            }).ConfigureAwait(false);
            LogPublished(reason, sourceTopic, partition, offset, dlqTopic, dr.Offset.Value);
        }
        catch (Exception ex)
        {
            LogPublishFailed(ex, reason, sourceTopic, partition, offset, dlqTopic);
        }
    }

    internal bool IsProducerDisposed => Volatile.Read(ref _producerDisposed) != 0;

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        // Bounded wait so shutdown never blocks long; if leases are still live, the last one to be
        // released disposes the producer (see Release), so their publish still succeeds.
        if (SpinWait.SpinUntil(() => Volatile.Read(ref _inFlight) == 0, DrainTimeout))
            DisposeProducer();
    }

    private void DisposeProducer()
    {
        if (Interlocked.Exchange(ref _producerDisposed, 1) != 0) return;
        try
        {
            _producer.Flush(FlushTimeout);
        }
        catch
        {
            // best-effort flush during teardown
        }
        _producer.Dispose();
    }

    [LoggerMessage(EventId = 12000, Level = LogLevel.Warning,
        Message = "Kafka DLQ: routed {Reason} record {Topic}[{Partition}]@{Offset} to {DlqTopic} at offset {DlqOffset}")]
    private partial void LogPublished(string reason, string topic, int partition, long offset, string dlqTopic, long dlqOffset);

    [LoggerMessage(EventId = 12001, Level = LogLevel.Error,
        Message = "Kafka DLQ: failed to publish {Reason} record {Topic}[{Partition}]@{Offset} to {DlqTopic}; the record is dropped")]
    private partial void LogPublishFailed(Exception ex, string reason, string topic, int partition, long offset, string dlqTopic);

    [LoggerMessage(EventId = 12008, Level = LogLevel.Warning,
        Message = "Kafka DLQ: {Reason} record {Topic}[{Partition}]@{Offset} arrived after the DLQ publisher was shut down; the record is dropped")]
    private partial void LogDroppedAfterDispose(string reason, string topic, int partition, long offset);
}
