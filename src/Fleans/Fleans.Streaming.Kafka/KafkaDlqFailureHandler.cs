using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Routes records whose subscriber kept failing to the dead-letter topic. Orleans retries delivery
/// itself until <c>StreamPullingAgentOptions.MaxEventDeliveryTime</c> (see
/// <see cref="KafkaStreamingOptions.DeliveryRetryWindow"/>), then calls <see cref="OnDeliveryFailure"/>
/// once and moves the cursor past the record — so this handler only has to capture it.
/// Stateless per queue: the re-consumer and publisher are shared, owned and disposed by the factory.
/// </summary>
internal sealed partial class KafkaDlqFailureHandler : IStreamFailureHandler
{
    private readonly string _topic;
    private readonly KafkaRecordReconsumer _reconsumer;
    private readonly KafkaDeadLetterPublisher _publisher;
    private readonly ILogger<KafkaDlqFailureHandler> _logger;

    public bool ShouldFaultSubsriptionOnError => false;

    internal KafkaDlqFailureHandler(
        string topic, KafkaRecordReconsumer reconsumer, KafkaDeadLetterPublisher publisher, ILoggerFactory loggerFactory)
    {
        _topic = topic;
        _reconsumer = reconsumer;
        _publisher = publisher;
        _logger = loggerFactory.CreateLogger<KafkaDlqFailureHandler>();
    }

    public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName,
        StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        if (sequenceToken is not KafkaSequenceToken kt)
        {
            LogMissingSequenceToken(streamIdentity.ToString());
            return Task.CompletedTask;
        }

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
        KafkaReconsumeOutcome outcome;
        Confluent.Kafka.Message<byte[], byte[]>? message;
        try
        {
            (outcome, message) = await _reconsumer.FetchAsync(_topic, kt.Partition, kt.Offset).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogReconsumeFailed(ex, _topic, kt.Partition, kt.Offset);
            return;
        }

        switch (outcome)
        {
            case KafkaReconsumeOutcome.Found:
                await _publisher.PublishAsync(
                    _topic, kt.Partition, kt.Offset, message!.Key, message.Value,
                    KafkaDeadLetterReason.DeliveryFailed, streamId).ConfigureAwait(false);
                break;
            case KafkaReconsumeOutcome.OffsetGone:
                LogOffsetGone(_topic, kt.Partition, kt.Offset);
                break;
            case KafkaReconsumeOutcome.TimedOut:
                LogReconsumeTimedOut(_topic, kt.Partition, kt.Offset);
                break;
            case KafkaReconsumeOutcome.Disposed:
                LogShuttingDown(_topic, kt.Partition, kt.Offset);
                break;
        }
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

    [LoggerMessage(EventId = 12007, Level = LogLevel.Warning,
        Message = "Kafka DLQ: delivery of {Topic}[{Partition}]@{Offset} failed after the DLQ was shut down; dropped without dead-lettering")]
    private partial void LogShuttingDown(string topic, int partition, long offset);
}
