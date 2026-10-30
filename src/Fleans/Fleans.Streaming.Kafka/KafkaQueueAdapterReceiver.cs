using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

internal sealed partial class KafkaQueueAdapterReceiver : IQueueAdapterReceiver
{
    private readonly QueueId _queueId;
    private readonly KafkaStreamingOptions _options;
    private readonly Serializer<KafkaBatchContainer> _serializer;
    private readonly ILogger<KafkaQueueAdapterReceiver> _logger;
    private readonly string _topic;
    private readonly KafkaDeadLetterPublisher? _deadLetters;

    private IConsumer<byte[], byte[]>? _consumer;
    private bool _assigned;
    private long _lastDeliveredOffset = -1;

    public KafkaQueueAdapterReceiver(
        QueueId queueId,
        KafkaStreamingOptions options,
        Serializer<KafkaBatchContainer> serializer,
        ILoggerFactory loggerFactory,
        KafkaDeadLetterPublisher? deadLetters)
    {
        _queueId = queueId;
        _options = options;
        _serializer = serializer;
        _logger = loggerFactory.CreateLogger<KafkaQueueAdapterReceiver>();
        _topic = KafkaTopicNaming.TopicForQueue(options, queueId);
        _deadLetters = deadLetters;
    }

    public async Task Initialize(TimeSpan timeout)
    {
        var config = new ConsumerConfig
        {
            GroupId = _options.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnablePartitionEof = false,
        };
        if (_options.SecurityProtocol is KafkaSecurityProtocol.Ssl or KafkaSecurityProtocol.SaslSsl
            && string.IsNullOrEmpty(_options.SslCaLocation))
        {
            LogSslNoPathsOsTrustStore(_options.SecurityProtocol);
        }
        _consumer = KafkaClientConfigExtensions.BuildConsumer(config, _options, e => LogConsumerError(_topic, e.Reason));
        // Orleans keeps pumping a receiver whose Initialize failed; GetQueueMessagesAsync retries the assignment.
        await AssignPartitionsAsync(timeout);
    }

    /// <summary>
    /// Reads every partition of the topic from the group's committed offsets, without joining the
    /// consumer group (#954). The Orleans queue balancer already gives each queue to one silo; a
    /// group <c>Subscribe</c> added Kafka's own balancer on top, and a consumer closed mid-join (a
    /// joining silo drops queues ~100 ms after taking them) held every queue of every silo in a
    /// rebalance until the broker's session timeout. Commits still go to <see cref="KafkaStreamingOptions.ConsumerGroup"/>,
    /// so the next owner of the queue resumes where this one stopped.
    /// </summary>
    private async Task AssignPartitionsAsync(TimeSpan timeout)
    {
        var consumer = _consumer!;
        // GetMetadata blocks; keep it off the pulling agent's scheduler.
        var metadata = await Task.Run(() =>
        {
            using var admin = new DependentAdminClientBuilder(consumer.Handle).Build();
            return admin.GetMetadata(_topic, timeout);
        });
        var topic = metadata.Topics.SingleOrDefault(t => t.Topic == _topic);
        if (topic is null || topic.Error.IsError || topic.Partitions.Count == 0)
            throw new KafkaException(topic?.Error ?? new Error(Confluent.Kafka.ErrorCode.UnknownTopicOrPart));

        // Shutdown may have disposed the consumer during the await.
        if (!ReferenceEquals(_consumer, consumer))
            return;
        consumer.Assign(topic.Partitions.Select(p => new TopicPartitionOffset(_topic, p.PartitionId, Offset.Stored)));
        _assigned = true;
        LogPartitionsAssigned(_topic, topic.Partitions.Count);
    }

    [LoggerMessage(EventId = 11115, Level = LogLevel.Debug,
        Message = "Kafka receiver reads all {PartitionCount} partition(s) of topic {Topic}")]
    private partial void LogPartitionsAssigned(string topic, int partitionCount);

    [LoggerMessage(EventId = 11100, Level = LogLevel.Warning,
        Message = "SecurityProtocol={SecurityProtocol} configured without explicit SSL paths — broker certificate will be validated against the OS trust store.")]
    private partial void LogSslNoPathsOsTrustStore(KafkaSecurityProtocol securityProtocol);

    [LoggerMessage(EventId = 11107, Level = LogLevel.Error,
        Message = "Kafka receiver: failed to deserialize record {Topic}[{Partition}]@{Offset}; the record is skipped and not dead-lettered (EnableDeadLetterQueue is off)")]
    private partial void LogDeserializationFailed(Exception ex, string topic, int partition, long offset);

    [LoggerMessage(EventId = 11113, Level = LogLevel.Error,
        Message = "Kafka receiver: failed to deserialize record {Topic}[{Partition}]@{Offset}; routing it to the dead-letter topic")]
    private partial void LogDeserializationFailedDeadLettering(Exception ex, string topic, int partition, long offset);

    [LoggerMessage(EventId = 11108, Level = LogLevel.Warning,
        Message = "Kafka consumer error on topic {Topic}: {Reason}")]
    private partial void LogConsumerError(string topic, string reason);

    [LoggerMessage(EventId = 11109, Level = LogLevel.Warning,
        Message = "Kafka consume failed on topic {Topic}")]
    private partial void LogConsumeFailed(Exception ex, string topic);

    [LoggerMessage(EventId = 11110, Level = LogLevel.Warning,
        Message = "Kafka offset commit failed on topic {Topic}")]
    private partial void LogCommitFailed(Exception ex, string topic);

    [LoggerMessage(EventId = 11111, Level = LogLevel.Warning,
        Message = "Kafka consumer close failed on topic {Topic}")]
    private partial void LogCloseFailed(Exception ex, string topic);

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        IList<IBatchContainer> batches = new List<IBatchContainer>();
        if (_consumer is null)
        {
            return batches;
        }
        if (!_assigned)
        {
            // Throws while the broker is unreachable; the pulling agent retries with backoff.
            await AssignPartitionsAsync(_options.AdminTimeout);
            if (_consumer is null)
            {
                return batches;
            }
        }

        var deadline = DateTime.UtcNow + _options.PollTimeout;
        var limit = maxCount <= 0 ? int.MaxValue : maxCount;
        Dictionary<TopicPartition, long>? lastRead = null;
        for (var i = 0; i < limit; i++)
        {
            var remaining = deadline - DateTime.UtcNow;
            ConsumeResult<byte[], byte[]>? result;
            try
            {
                result = _consumer.Consume(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
            catch (ConsumeException ex)
            {
                LogConsumeFailed(ex, _topic);
                break;
            }

            if (result is null || result.Message is null)
            {
                break;
            }
            lastRead ??= new Dictionary<TopicPartition, long>();
            lastRead[result.TopicPartition] = result.Offset.Value;

            try
            {
                var container = _serializer.Deserialize(result.Message.Value);
                container.Token = new KafkaSequenceToken(result.Offset.Value, 0, result.Partition.Value);
                batches.Add(container);
                _lastDeliveredOffset = result.Offset.Value;
            }
            catch (Exception ex)
            {
                // Typically a type this silo does not have (#866). Skipped either way. The next Commit()
                // moves past it; if the silo restarts before one, it is read (and logged / dead-lettered) again.
                if (_deadLetters is null)
                {
                    LogDeserializationFailed(ex, _topic, result.Partition.Value, result.Offset.Value);
                    continue;
                }
                LogDeserializationFailedDeadLettering(ex, _topic, result.Partition.Value, result.Offset.Value);
                // No ConfigureAwait(false): stay on the pulling agent's scheduler.
                await _deadLetters.PublishAsync(_topic, result.Partition.Value, result.Offset.Value, result.Message,
                    KafkaDeadLetterPublisher.ReasonDeserializationFailed,
                    [new("x-fleans-exception", $"{ex.GetType().FullName}: {ex.Message}")]);
                // Return what we have: Shutdown may have run during the await and disposed the consumer, and
                // the records already read must not wait behind more publishes. One DLQ publish per poll.
                break;
            }
        }

        RecordConsumerLag(lastRead);
        return batches;
    }

    private void RecordConsumerLag(Dictionary<TopicPartition, long>? lastRead)
    {
        // Shutdown may have disposed the consumer during a dead-letter await above.
        if (lastRead is null || _consumer is not { } consumer)
        {
            return;
        }
        try
        {
            foreach (var (partition, offset) in lastRead)
            {
                KafkaConsumerLag.Record(consumer, partition, offset);
            }
        }
        catch (Exception ex) when (ex is KafkaException or ObjectDisposedException)
        {
            // A metric never fails a poll; the next poll samples again.
        }
    }

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        if (_consumer is null || messages.Count == 0)
        {
            return Task.CompletedTask;
        }
        try
        {
            // Commit checkpoint AFTER handler invocation completes — at-least-once semantics.
            _consumer.Commit();
        }
        catch (KafkaException ex)
        {
            LogCommitFailed(ex, _topic);
        }
        return Task.CompletedTask;
    }

    public Task Shutdown(TimeSpan timeout)
    {
        if (_consumer is null)
        {
            return Task.CompletedTask;
        }
        try
        {
            _consumer.Close();
        }
        catch (Exception ex)
        {
            LogCloseFailed(ex, _topic);
        }
        try { _consumer.Dispose(); } catch { /* swallow */ }
        _consumer = null;
        return Task.CompletedTask;
    }
}
