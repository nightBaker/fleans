using System.Collections.Concurrent;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

internal sealed partial class KafkaDlqFailureHandler : IStreamFailureHandler, IDisposable
{
    private readonly string _topic;
    private readonly KafkaStreamingOptions _options;
    private readonly ILogger<KafkaDlqFailureHandler> _logger;
    private readonly IProducer<byte[], byte[]> _producer;
    private readonly IConsumer<Ignore, Ignore> _commitConsumer;
    private readonly Lazy<IConsumer<Ignore, byte[]>> _reConsumeConsumer;

    // Keyed by "{offset}:{partition}" — count of OnSubscriptionFailure calls per Kafka message.
    private readonly ConcurrentDictionary<string, int> _retryCounts = new();

    // Serializes re-consume calls: _reConsumeConsumer.Value.Assign + .Consume is not thread-safe.
    private readonly SemaphoreSlim _reConsumeLock = new(1, 1);

    // Keyed by "{offset}:{partition}" — set once DLQ routing fires so in-session retries are no-ops.
    // Entry count = distinct poison-message triplets per silo lifetime; no GC pass needed because
    // poison messages are assumed exceptional (memory cost = O(distinct poison-message triplets)).
    private readonly ConcurrentDictionary<string, bool> _routedToDlq = new();

    private int _disposed;

    public bool ShouldFaultSubsriptionOnError => false;

    internal KafkaDlqFailureHandler(string topic, KafkaStreamingOptions options, ILoggerFactory loggerFactory)
    {
        _topic = topic;
        _options = options;
        _logger = loggerFactory.CreateLogger<KafkaDlqFailureHandler>();

        _producer = new ProducerBuilder<byte[], byte[]>(new ProducerConfig
        {
            BootstrapServers = options.Brokers,
            Acks = Acks.All,
            EnableIdempotence = true,
        }).Build();

        _commitConsumer = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig
        {
            BootstrapServers = options.Brokers,
            GroupId = options.ConsumerGroup,
            EnableAutoCommit = false,
        }).Build();

        // Single re-consume consumer reused across DLQ events; Assign is called before each Consume.
        _reConsumeConsumer = new Lazy<IConsumer<Ignore, byte[]>>(() =>
            new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
            {
                BootstrapServers = options.Brokers,
                GroupId = $"{options.ConsumerGroup}-dlq-reconsume",
                EnableAutoCommit = false,
                AutoOffsetReset = AutoOffsetReset.Earliest,
            }).Build());
    }

    public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName,
        StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        LogDeliveryFailureDropped(subscriptionId.ToString(), streamIdentity.ToString());
        return Task.CompletedTask;
    }

    public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName,
        StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        var kt = sequenceToken as KafkaSequenceToken;
        var msgKey = kt is null
            ? $"{streamIdentity}:{sequenceToken?.ToString() ?? "null"}"
            : $"{kt.SequenceNumber}:{kt.Partition}";

        if (_routedToDlq.ContainsKey(msgKey)) return Task.CompletedTask;

        var retryCount = _retryCounts.AddOrUpdate(msgKey, 1, (_, c) => c + 1);

        if (retryCount < _options.MaxConsumerRetries)
        {
            LogRetryAttempt(retryCount, _options.MaxConsumerRetries, streamIdentity.ToString());
            return Task.CompletedTask;
        }

        if (!_routedToDlq.TryAdd(msgKey, true)) return Task.CompletedTask;
        _retryCounts.TryRemove(msgKey, out _);

        if (kt is null)
        {
            LogMissingSequenceToken(streamIdentity.ToString());
            return Task.CompletedTask;
        }

        return RouteToDlqAsync(streamIdentity.ToString(), kt);
    }

    private async Task RouteToDlqAsync(string streamId, KafkaSequenceToken kt)
    {
        var dlqTopic = KafkaTopicNaming.DeadLetterTopicForSource(_options, _topic);
        byte[]? rawValue;
        await _reConsumeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            rawValue = await Task.Run(() => ReconsumeRawBytes(kt)).ConfigureAwait(false);
        }
        catch (ConsumeException cex) when (cex.Error.Code == Confluent.Kafka.ErrorCode.Local_NoOffset)
        {
            LogOffsetGone(kt.SequenceNumber, kt.Partition);
            CommitOffset(kt.Partition, kt.SequenceNumber + 1);
            return;
        }
        catch (Exception ex)
        {
            LogReconsumeFailedForDlq(ex, kt.SequenceNumber, kt.Partition);
            CommitOffset(kt.Partition, kt.SequenceNumber + 1);
            return;
        }
        finally
        {
            _reConsumeLock.Release();
        }

        if (rawValue is null)
        {
            LogReconsumeTimedOut(kt.SequenceNumber, kt.Partition);
            CommitOffset(kt.Partition, kt.SequenceNumber + 1);
            return;
        }

        var msg = new Message<byte[], byte[]>
        {
            Value = rawValue,
            Headers = new Headers
            {
                { "x-fleans-original-topic",     Encoding.UTF8.GetBytes(_topic) },
                { "x-fleans-original-partition",  Encoding.UTF8.GetBytes(kt.Partition.ToString()) },
                { "x-fleans-original-offset",     Encoding.UTF8.GetBytes(kt.SequenceNumber.ToString()) },
                { "x-fleans-retry-count",         Encoding.UTF8.GetBytes(_options.MaxConsumerRetries.ToString()) },
                { "x-fleans-failure-time-utc",    Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")) },
                { "x-fleans-stream-id",           Encoding.UTF8.GetBytes(streamId) },
            }
        };

        try
        {
            var dr = await _producer.ProduceAsync(dlqTopic, msg).ConfigureAwait(false);
            LogDlqPublished(streamId, kt.SequenceNumber, kt.Partition, dlqTopic, dr.Offset.Value);
        }
        catch (Exception ex)
        {
            LogDlqPublishFailed(ex, kt.SequenceNumber, kt.Partition, dlqTopic);
        }
        finally
        {
            // Always commit offset+1 regardless of DLQ publish success.
            // If CommitOffset fails, the message may be DLQ'd again on the next silo restart
            // (at-least-once DLQ delivery). Operators can detect this via EventId 12013.
            CommitOffset(kt.Partition, kt.SequenceNumber + 1);
        }
    }

    private byte[]? ReconsumeRawBytes(KafkaSequenceToken kt)
    {
        var consumer = _reConsumeConsumer.Value;
        consumer.Assign(new TopicPartitionOffset(_topic, kt.Partition, kt.SequenceNumber));
        var result = consumer.Consume(TimeSpan.FromSeconds(10));
        return result?.Message?.Value;
    }

    private void CommitOffset(int partition, long nextOffset)
    {
        try
        {
            _commitConsumer.Commit(new[]
            {
                new TopicPartitionOffset(_topic, partition, new Offset(nextOffset))
            });
            LogOffsetCommitted(nextOffset - 1, partition, nextOffset);
        }
        catch (Exception ex)
        {
            LogOffsetCommitFailed(ex, nextOffset - 1, partition);
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        _commitConsumer.Dispose();
        if (_reConsumeConsumer.IsValueCreated)
            _reConsumeConsumer.Value.Dispose();
        _reConsumeLock.Dispose();
    }

    [LoggerMessage(EventId = 12006, Level = LogLevel.Debug,
        Message = "DLQ handler: retry {RetryCount}/{MaxRetries} for stream {StreamId}")]
    private partial void LogRetryAttempt(int retryCount, int maxRetries, string streamId);

    [LoggerMessage(EventId = 12007, Level = LogLevel.Warning,
        Message = "DLQ handler: delivery failure for subscription {SubscriptionId} stream {StreamId} dropped")]
    private partial void LogDeliveryFailureDropped(string subscriptionId, string streamId);

    [LoggerMessage(EventId = 12008, Level = LogLevel.Warning,
        Message = "DLQ handler: sequence token is not KafkaSequenceToken for stream {StreamId} — cannot determine Kafka offset; routing skipped")]
    private partial void LogMissingSequenceToken(string streamId);

    [LoggerMessage(EventId = 12009, Level = LogLevel.Warning,
        Message = "DLQ handler: offset {Offset} partition {Partition} no longer available for re-consume; committing offset+1 and skipping DLQ publish")]
    private partial void LogOffsetGone(long offset, int partition);

    [LoggerMessage(EventId = 12010, Level = LogLevel.Error,
        Message = "DLQ handler: re-consume failed for offset {Offset} partition {Partition}; committing offset+1 and skipping DLQ publish")]
    private partial void LogReconsumeFailedForDlq(Exception ex, long offset, int partition);

    [LoggerMessage(EventId = 12011, Level = LogLevel.Warning,
        Message = "DLQ handler: re-consume timed out at offset {Offset} partition {Partition}; committing offset+1 and skipping DLQ publish")]
    private partial void LogReconsumeTimedOut(long offset, int partition);

    [LoggerMessage(EventId = 12012, Level = LogLevel.Information,
        Message = "DLQ handler: published stream {StreamId} offset {Offset} partition {Partition} to {DlqTopic} at DLQ offset {DlqOffset}")]
    private partial void LogDlqPublished(string streamId, long offset, int partition, string dlqTopic, long dlqOffset);

    [LoggerMessage(EventId = 12013, Level = LogLevel.Error,
        Message = "DLQ handler: offset commit failed for offset {Offset} partition {Partition} — offset not advanced; message may be DLQ'd again on next restart")]
    private partial void LogOffsetCommitFailed(Exception ex, long offset, int partition);

    [LoggerMessage(EventId = 12014, Level = LogLevel.Debug,
        Message = "DLQ handler: committed source offset {SourceOffset} partition {Partition}; next start = {NextOffset}")]
    private partial void LogOffsetCommitted(long sourceOffset, int partition, long nextOffset);

    [LoggerMessage(EventId = 12015, Level = LogLevel.Error,
        Message = "DLQ handler: failed to publish offset {Offset} partition {Partition} to DLQ topic {DlqTopic}")]
    private partial void LogDlqPublishFailed(Exception ex, long offset, int partition, string dlqTopic);
}
