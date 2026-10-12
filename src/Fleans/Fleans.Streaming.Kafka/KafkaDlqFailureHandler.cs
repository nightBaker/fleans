using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Routes records whose subscriber kept failing to the dead-letter topic. Orleans retries delivery
/// itself until <c>StreamPullingAgentOptions.MaxEventDeliveryTime</c> (see
/// <see cref="KafkaStreamingOptions.DeliveryRetryWindow"/>), then calls <see cref="OnDeliveryFailure"/>
/// once and moves the cursor past the record — so this handler only has to capture it.
/// </summary>
internal sealed partial class KafkaDlqFailureHandler : IStreamFailureHandler, IDisposable
{
    private readonly string _topic;
    private readonly KafkaDeadLetterPublisher _publisher;
    private readonly ILogger<KafkaDlqFailureHandler> _logger;
    private readonly Lazy<IConsumer<byte[], byte[]>> _reConsumeConsumer;

    // Serializes re-consume calls: Assign + Consume on one consumer is not thread-safe.
    private readonly SemaphoreSlim _reConsumeLock = new(1, 1);

    // "{partition}:{offset}" already routed — a record failing for several subscribers is dead-lettered once.
    private readonly ConcurrentDictionary<string, byte> _routed = new();

    private int _disposed;

    public bool ShouldFaultSubsriptionOnError => false;

    internal KafkaDlqFailureHandler(
        string topic, KafkaStreamingOptions options, KafkaDeadLetterPublisher publisher, ILoggerFactory loggerFactory)
    {
        _topic = topic;
        _publisher = publisher;
        _logger = loggerFactory.CreateLogger<KafkaDlqFailureHandler>();

        // Assign-only consumer (never subscribes, never commits) used to fetch the original raw bytes.
        _reConsumeConsumer = new Lazy<IConsumer<byte[], byte[]>>(() =>
        {
            var config = new ConsumerConfig
            {
                BootstrapServers = options.Brokers,
                GroupId = $"{options.ConsumerGroup}-dlq-reconsume",
                EnableAutoCommit = false,
                AutoOffsetReset = AutoOffsetReset.Earliest,
            };
            KafkaClientConfigExtensions.ApplySecurity(config, options);
            var builder = new ConsumerBuilder<byte[], byte[]>(config);
            if (options.OAuthBearerTokenProvider is not null)
                builder.SetOAuthBearerTokenRefreshHandler(options.OAuthBearerTokenProvider);
            return builder.Build();
        });
    }

    public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName,
        StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        if (sequenceToken is not KafkaSequenceToken kt)
        {
            LogMissingSequenceToken(streamIdentity.ToString());
            return Task.CompletedTask;
        }

        if (!_routed.TryAdd($"{kt.Partition}:{kt.SequenceNumber}", 0))
            return Task.CompletedTask;

        return RouteToDlqAsync(streamIdentity.ToString(), kt);
    }

    public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName,
        StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        // Subscription-initialisation failure, not a poison record — nothing to dead-letter.
        LogSubscriptionFailure(subscriptionId.ToString(), streamIdentity.ToString());
        return Task.CompletedTask;
    }

    private async Task RouteToDlqAsync(string streamId, KafkaSequenceToken kt)
    {
        ConsumeResult<byte[], byte[]>? record;
        await _reConsumeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            record = await Task.Run(() => Reconsume(kt)).ConfigureAwait(false);
        }
        catch (ConsumeException cex) when (cex.Error.Code == Confluent.Kafka.ErrorCode.Local_NoOffset)
        {
            LogOffsetGone(_topic, kt.Partition, kt.SequenceNumber);
            return;
        }
        catch (Exception ex)
        {
            LogReconsumeFailed(ex, _topic, kt.Partition, kt.SequenceNumber);
            return;
        }
        finally
        {
            _reConsumeLock.Release();
        }

        if (record?.Message?.Value is null)
        {
            LogReconsumeTimedOut(_topic, kt.Partition, kt.SequenceNumber);
            return;
        }

        await _publisher.PublishAsync(
            _topic, kt.Partition, kt.SequenceNumber, record.Message.Key, record.Message.Value,
            KafkaDeadLetterReason.DeliveryFailed, streamId).ConfigureAwait(false);
    }

    private ConsumeResult<byte[], byte[]>? Reconsume(KafkaSequenceToken kt)
    {
        var consumer = _reConsumeConsumer.Value;
        consumer.Assign(new TopicPartitionOffset(_topic, kt.Partition, kt.SequenceNumber));
        var result = consumer.Consume(TimeSpan.FromSeconds(10));
        // A compacted/deleted offset makes the consumer skip ahead — never dead-letter the wrong record.
        return result is not null && result.Offset.Value == kt.SequenceNumber ? result : null;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        if (_reConsumeConsumer.IsValueCreated)
            _reConsumeConsumer.Value.Dispose();
        _reConsumeLock.Dispose();
    }

    [LoggerMessage(EventId = 12002, Level = LogLevel.Warning,
        Message = "Kafka DLQ: subscription {SubscriptionId} on stream {StreamId} failed to initialise; not a poison record, nothing dead-lettered")]
    private partial void LogSubscriptionFailure(string subscriptionId, string streamId);

    [LoggerMessage(EventId = 12003, Level = LogLevel.Warning,
        Message = "Kafka DLQ: delivery failed on stream {StreamId} but the sequence token is not a KafkaSequenceToken; record cannot be located and is dropped")]
    private partial void LogMissingSequenceToken(string streamId);

    [LoggerMessage(EventId = 12004, Level = LogLevel.Warning,
        Message = "Kafka DLQ: record {Topic}[{Partition}]@{Offset} is no longer retained by the broker; dropped without dead-lettering")]
    private partial void LogOffsetGone(string topic, int partition, long offset);

    [LoggerMessage(EventId = 12005, Level = LogLevel.Error,
        Message = "Kafka DLQ: re-consume of {Topic}[{Partition}]@{Offset} failed; dropped without dead-lettering")]
    private partial void LogReconsumeFailed(Exception ex, string topic, int partition, long offset);

    [LoggerMessage(EventId = 12006, Level = LogLevel.Warning,
        Message = "Kafka DLQ: re-consume of {Topic}[{Partition}]@{Offset} returned nothing within 10s; dropped without dead-lettering")]
    private partial void LogReconsumeTimedOut(string topic, int partition, long offset);
}
