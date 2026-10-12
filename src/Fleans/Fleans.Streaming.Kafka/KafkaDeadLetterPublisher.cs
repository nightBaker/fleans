using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Fleans.Streaming.Kafka;

/// <summary>Why a message was routed to the dead-letter topic; written to the <c>x-fleans-failure-reason</c> header.</summary>
internal static class KafkaDeadLetterReason
{
    public const string DeliveryFailed = "delivery-failed";
    public const string DeserializationFailed = "deserialization-failed";
}

/// <summary>
/// Publishes the original raw Kafka record to <c>&lt;topic&gt;&lt;DeadLetterTopicSuffix&gt;</c> with diagnostic headers.
/// One instance per adapter factory, shared by the receivers (deserialization failures) and the
/// delivery-failure handlers (subscriber failures).
/// </summary>
internal sealed partial class KafkaDeadLetterPublisher : IDisposable
{
    private readonly KafkaStreamingOptions _options;
    private readonly ILogger<KafkaDeadLetterPublisher> _logger;
    private readonly IProducer<byte[], byte[]> _producer;
    private int _disposed;

    public KafkaDeadLetterPublisher(KafkaStreamingOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<KafkaDeadLetterPublisher>();

        var producerBuilder = new ProducerBuilder<byte[], byte[]>(KafkaQueueAdapter.BuildProducerConfig(options));
        if (options.OAuthBearerTokenProvider is not null)
            producerBuilder.SetOAuthBearerTokenRefreshHandler(options.OAuthBearerTokenProvider);
        _producer = producerBuilder.Build();
    }

    /// <summary>Never throws — a failed DLQ publish is logged (EventId 12001) and the record is dropped.</summary>
    public async Task PublishAsync(
        string sourceTopic, int partition, long offset, byte[]? key, byte[] value,
        string reason, string? streamId = null, Exception? exception = null)
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
        if (exception is not null)
        {
            headers.Add("x-fleans-exception-type", Utf8(exception.GetType().FullName ?? exception.GetType().Name));
            headers.Add("x-fleans-exception-message", Utf8(exception.Message));
        }

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

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(10));
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
}
