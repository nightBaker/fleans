using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using ErrorCode = Confluent.Kafka.ErrorCode;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Routes records whose subscriber kept failing to the dead-letter topic. Orleans retries delivery
/// itself until <c>StreamPullingAgentOptions.MaxEventDeliveryTime</c> (see
/// <see cref="KafkaStreamingOptions.DeliveryRetryWindow"/>), then calls <see cref="OnDeliveryFailure"/>
/// once per failing subscription and moves the cursor past the record — so this handler only has to
/// capture it: re-read the raw record with a short-lived consumer and publish it with the adapter's
/// producer. Failures are rare, so it owns no long-lived Kafka clients and needs no shutdown logic.
/// </summary>
internal sealed partial class KafkaDlqFailureHandler : IStreamFailureHandler
{
    // Covers a cold client: connect, TLS, SASL/OAuth token fetch and metadata, then the fetch itself.
    internal static readonly TimeSpan ConsumeTimeout = TimeSpan.FromSeconds(30);

    // Bounds how long the pulling agent awaiting OnDeliveryFailure waits for the DLQ ack. Cancelling
    // only stops the wait: the shared producer keeps the message (up to its message.timeout.ms) and
    // may still deliver it.
    internal static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(30);

    private readonly string _topic;
    private readonly KafkaStreamingOptions _options;
    private readonly KafkaQueueAdapter _adapter;
    private readonly ILogger<KafkaDlqFailureHandler> _logger;

    public bool ShouldFaultSubsriptionOnError => false;

    internal KafkaDlqFailureHandler(
        string topic, KafkaStreamingOptions options, KafkaQueueAdapter adapter, ILoggerFactory loggerFactory)
    {
        _topic = topic;
        _options = options;
        _adapter = adapter;
        _logger = loggerFactory.CreateLogger<KafkaDlqFailureHandler>();
    }

    public async Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName,
        StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        if (sequenceToken is not KafkaSequenceToken kt)
        {
            LogMissingSequenceToken(streamIdentity.ToString());
            return;
        }

        ConsumeResult<byte[], byte[]>? record;
        try
        {
            // Consume blocks; keep it off the agent's scheduler.
            record = await Task.Run(() => Reconsume(kt.Partition, kt.Offset)).ConfigureAwait(false);
        }
        catch (ConsumeException ex) when (ex.Error.Code is ErrorCode.Local_AutoOffsetReset
                                              or ErrorCode.OffsetOutOfRange
                                              or ErrorCode.Local_NoOffset)
        {
            LogOffsetGone(_topic, kt.Partition, kt.Offset);
            return;
        }
        catch (Exception ex)
        {
            LogReconsumeFailed(ex, _topic, kt.Partition, kt.Offset);
            return;
        }

        if (record?.Message is null)
        {
            LogReconsumeTimedOut(_topic, kt.Partition, kt.Offset, ConsumeTimeout.TotalSeconds);
            return;
        }
        // A compacted offset makes the consumer skip ahead — never dead-letter the wrong record.
        if (record.Offset.Value != kt.Offset)
        {
            LogOffsetGone(_topic, kt.Partition, kt.Offset);
            return;
        }

        await PublishAsync(kt, record.Message, streamIdentity.ToString(), subscriptionId.ToString()).ConfigureAwait(false);
    }

    public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName,
        StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        // Subscription-initialisation failure, not a poison record — nothing to dead-letter.
        LogSubscriptionFailure(subscriptionId.ToString(), streamIdentity.ToString());
        return Task.CompletedTask;
    }

    /// <summary>Returns <c>null</c> on timeout; throws <see cref="ConsumeException"/> if the offset is gone.</summary>
    private ConsumeResult<byte[], byte[]>? Reconsume(int partition, long offset)
    {
        var config = new ConsumerConfig
        {
            GroupId = $"{_options.ConsumerGroup}-dlq-reconsume",
            EnableAutoCommit = false,
            // Error, not Earliest: a purged offset must surface as an error, not jump to another record.
            AutoOffsetReset = AutoOffsetReset.Error,
        };

        // Assign-only: never joins the group, never commits.
        using var consumer = KafkaClientConfigExtensions.BuildConsumer(
            config, _options, e => LogReconsumeClientError(_topic, e.Reason));
        consumer.Assign(new TopicPartitionOffset(_topic, partition, offset));
        return consumer.Consume(ConsumeTimeout);
    }

    private async Task PublishAsync(KafkaSequenceToken kt, Message<byte[], byte[]> record, string streamId, string subscriptionId)
    {
        var dlqTopic = KafkaTopicNaming.DeadLetterTopicForSource(_options, _topic);
        var message = new Message<byte[], byte[]>
        {
            Key = record.Key,
            Value = record.Value,
            Headers = new Headers
            {
                { "x-fleans-original-topic", Utf8(_topic) },
                { "x-fleans-original-partition", Utf8(kt.Partition.ToString(CultureInfo.InvariantCulture)) },
                { "x-fleans-original-offset", Utf8(kt.Offset.ToString(CultureInfo.InvariantCulture)) },
                { "x-fleans-failure-reason", Utf8("delivery-failed") },
                { "x-fleans-failure-time-utc", Utf8(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)) },
                { "x-fleans-stream-id", Utf8(streamId) },
                { "x-fleans-subscription-id", Utf8(subscriptionId) },
            },
        };

        try
        {
            using var cts = new CancellationTokenSource(PublishTimeout);
            var dr = await _adapter.ProduceRawAsync(dlqTopic, message, cts.Token).ConfigureAwait(false);
            LogPublished(_topic, kt.Partition, kt.Offset, dlqTopic, dr.Offset.Value);
        }
        catch (Exception ex)
        {
            LogPublishFailed(ex, _topic, kt.Partition, kt.Offset, dlqTopic, PublishTimeout.TotalSeconds);
        }
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    [LoggerMessage(EventId = 12000, Level = LogLevel.Warning,
        Message = "Kafka DLQ: routed record {Topic}[{Partition}]@{Offset} to {DlqTopic} at offset {DlqOffset}")]
    private partial void LogPublished(string topic, int partition, long offset, string dlqTopic, long dlqOffset);

    [LoggerMessage(EventId = 12001, Level = LogLevel.Error,
        Message = "Kafka DLQ: publish of record {Topic}[{Partition}]@{Offset} to {DlqTopic} failed or was not acknowledged within {TimeoutSeconds}s; the producer may still deliver it, otherwise the record is lost")]
    private partial void LogPublishFailed(Exception ex, string topic, int partition, long offset, string dlqTopic, double timeoutSeconds);

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
        Message = "Kafka DLQ: re-consume of {Topic}[{Partition}]@{Offset} returned nothing within {TimeoutSeconds}s; dropped without dead-lettering")]
    private partial void LogReconsumeTimedOut(string topic, int partition, long offset, double timeoutSeconds);

    [LoggerMessage(EventId = 12007, Level = LogLevel.Warning,
        Message = "Kafka DLQ: re-consume client error on topic {Topic}: {Reason}")]
    private partial void LogReconsumeClientError(string topic, string reason);
}
