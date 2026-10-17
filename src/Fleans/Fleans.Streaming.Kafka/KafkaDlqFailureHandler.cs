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
/// capture it: re-read the raw record with a short-lived consumer and publish it with
/// <see cref="KafkaDeadLetterPublisher"/>. Failures are rare, so it owns no long-lived Kafka clients and
/// needs no shutdown logic.
/// </summary>
internal sealed partial class KafkaDlqFailureHandler : IStreamFailureHandler
{
    // Covers a cold client: connect, TLS, SASL/OAuth token fetch and metadata, then the fetch itself.
    internal static readonly TimeSpan ConsumeTimeout = TimeSpan.FromSeconds(30);

    private readonly string _topic;
    private readonly KafkaStreamingOptions _options;
    private readonly KafkaDeadLetterPublisher _publisher;
    private readonly ILogger<KafkaDlqFailureHandler> _logger;

    public bool ShouldFaultSubsriptionOnError => false;

    internal KafkaDlqFailureHandler(
        string topic, KafkaStreamingOptions options, KafkaDeadLetterPublisher publisher, ILoggerFactory loggerFactory)
    {
        _topic = topic;
        _options = options;
        _publisher = publisher;
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

        await _publisher.PublishAsync(_topic, kt.Partition, kt.Offset, record.Message,
            KafkaDeadLetterPublisher.ReasonDeliveryFailed,
            [
                new("x-fleans-stream-id", streamIdentity.ToString()),
                new("x-fleans-subscription-id", subscriptionId.ToString()),
            ]).ConfigureAwait(false);
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
