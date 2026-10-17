using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Publishes a raw source record to its dead-letter topic with the adapter's producer. Shared by
/// <see cref="KafkaDlqFailureHandler"/> (subscriber kept failing) and
/// <see cref="KafkaQueueAdapterReceiver"/> (record could not be deserialized on this silo).
/// </summary>
internal sealed partial class KafkaDeadLetterPublisher
{
    // Bounds how long the caller waits for the DLQ ack. Cancelling only stops the wait: the shared
    // producer keeps the message (up to its message.timeout.ms) and may still deliver it.
    internal static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(30);

    internal const string ReasonDeliveryFailed = "delivery-failed";
    internal const string ReasonDeserializationFailed = "deserialization-failed";

    private readonly KafkaStreamingOptions _options;
    private readonly KafkaQueueAdapter _adapter;
    private readonly ILogger<KafkaDeadLetterPublisher> _logger;

    internal KafkaDeadLetterPublisher(KafkaStreamingOptions options, KafkaQueueAdapter adapter, ILoggerFactory loggerFactory)
    {
        _options = options;
        _adapter = adapter;
        _logger = loggerFactory.CreateLogger<KafkaDeadLetterPublisher>();
    }

    /// <summary>Never throws: a publish failure is logged and the record is lost.</summary>
    internal async Task PublishAsync(
        string topic, int partition, long offset, Message<byte[], byte[]> record, string reason,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null)
    {
        var dlqTopic = KafkaTopicNaming.DeadLetterTopicForSource(_options, topic);
        // Keep the source record's own headers (trace context, an external writer's metadata) for triage.
        var headers = new Headers();
        foreach (var header in record.Headers ?? [])
            headers.Add(header.Key, header.GetValueBytes());
        headers.Add("x-fleans-original-topic", Utf8(topic));
        headers.Add("x-fleans-original-partition", Utf8(partition.ToString(CultureInfo.InvariantCulture)));
        headers.Add("x-fleans-original-offset", Utf8(offset.ToString(CultureInfo.InvariantCulture)));
        headers.Add("x-fleans-failure-reason", Utf8(reason));
        headers.Add("x-fleans-failure-time-utc", Utf8(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        foreach (var (key, value) in extraHeaders ?? [])
            headers.Add(key, Utf8(value));

        var message = new Message<byte[], byte[]> { Key = record.Key, Value = record.Value, Headers = headers };
        try
        {
            using var cts = new CancellationTokenSource(PublishTimeout);
            var dr = await _adapter.ProduceRawAsync(dlqTopic, message, cts.Token).ConfigureAwait(false);
            LogPublished(topic, partition, offset, dlqTopic, dr.Offset.Value, reason);
        }
        catch (Exception ex)
        {
            LogPublishFailed(ex, topic, partition, offset, dlqTopic, PublishTimeout.TotalSeconds);
        }
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    [LoggerMessage(EventId = 12000, Level = LogLevel.Warning,
        Message = "Kafka DLQ: routed record {Topic}[{Partition}]@{Offset} to {DlqTopic} at offset {DlqOffset} ({Reason})")]
    private partial void LogPublished(string topic, int partition, long offset, string dlqTopic, long dlqOffset, string reason);

    [LoggerMessage(EventId = 12001, Level = LogLevel.Error,
        Message = "Kafka DLQ: publish of record {Topic}[{Partition}]@{Offset} to {DlqTopic} failed or was not acknowledged within {TimeoutSeconds}s; the producer may still deliver it, otherwise the record is lost")]
    private partial void LogPublishFailed(Exception ex, string topic, int partition, long offset, string dlqTopic, double timeoutSeconds);
}
