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

    private IConsumer<byte[], byte[]>? _consumer;
    private long _lastDeliveredOffset = -1;

    public KafkaQueueAdapterReceiver(
        QueueId queueId,
        KafkaStreamingOptions options,
        Serializer<KafkaBatchContainer> serializer,
        ILoggerFactory loggerFactory)
    {
        _queueId = queueId;
        _options = options;
        _serializer = serializer;
        _logger = loggerFactory.CreateLogger<KafkaQueueAdapterReceiver>();
        _topic = KafkaTopicNaming.TopicForQueue(options, queueId);
    }

    public Task Initialize(TimeSpan timeout)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.Brokers,
            GroupId = _options.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnablePartitionEof = false,
        };
        KafkaClientConfigExtensions.ApplySecurity(config, _options);
        if (_options.SecurityProtocol is KafkaSecurityProtocol.Ssl or KafkaSecurityProtocol.SaslSsl
            && string.IsNullOrEmpty(_options.SslCaLocation))
        {
            LogSslNoPathsOsTrustStore(_options.SecurityProtocol);
        }
        var consumerBuilder = new ConsumerBuilder<byte[], byte[]>(config)
            .SetErrorHandler((_, e) => LogConsumerError(_topic, e.Reason));
        if (_options.OAuthBearerTokenProvider is not null)
            consumerBuilder.SetOAuthBearerTokenRefreshHandler(_options.OAuthBearerTokenProvider);
        _consumer = consumerBuilder.Build();
        _consumer.Subscribe(_topic);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 11100, Level = LogLevel.Warning,
        Message = "SecurityProtocol={SecurityProtocol} configured without explicit SSL paths — broker certificate will be validated against the OS trust store.")]
    private partial void LogSslNoPathsOsTrustStore(KafkaSecurityProtocol securityProtocol);

    [LoggerMessage(EventId = 11107, Level = LogLevel.Error,
        Message = "Kafka receiver: failed to deserialize record {Topic}[{Partition}]@{Offset}; the record is skipped and not dead-lettered")]
    private partial void LogDeserializationFailed(Exception ex, string topic, int partition, long offset);

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

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        IList<IBatchContainer> batches = new List<IBatchContainer>();
        if (_consumer is null)
        {
            return Task.FromResult(batches);
        }

        var deadline = DateTime.UtcNow + _options.PollTimeout;
        var limit = maxCount <= 0 ? int.MaxValue : maxCount;
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

            try
            {
                var container = _serializer.Deserialize(result.Message.Value);
                container.Token = new KafkaSequenceToken(result.Offset.Value, 0, result.Partition.Value);
                batches.Add(container);
                _lastDeliveredOffset = result.Offset.Value;
            }
            catch (Exception ex)
            {
                // Log-only, never dead-lettered: the next commit moves past it.
                LogDeserializationFailed(ex, _topic, result.Partition.Value, result.Offset.Value);
            }
        }

        return Task.FromResult(batches);
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
