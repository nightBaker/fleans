using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

internal sealed partial class KafkaQueueAdapter : IQueueAdapter, IDisposable
{
    private readonly KafkaStreamingOptions _options;
    private readonly IStreamQueueMapper _mapper;
    private readonly Serializer<KafkaBatchContainer> _serializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<KafkaQueueAdapter> _logger;
    private readonly IProducer<byte[], byte[]> _producer;
    private readonly IExternalEventEncoder? _encoder;

    public string Name { get; }

    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public KafkaQueueAdapter(
        string name,
        KafkaStreamingOptions options,
        IStreamQueueMapper mapper,
        Serializer<KafkaBatchContainer> serializer,
        ILoggerFactory loggerFactory,
        IExternalEventEncoder? encoder = null) : this(
            name, options, mapper, serializer, loggerFactory,
            BuildProducer(options), encoder)
    { }

    // Testability overload — kept internal; tests reach it via InternalsVisibleTo.
    internal KafkaQueueAdapter(
        string name,
        KafkaStreamingOptions options,
        IStreamQueueMapper mapper,
        Serializer<KafkaBatchContainer> serializer,
        ILoggerFactory loggerFactory,
        IProducer<byte[], byte[]> producer,
        IExternalEventEncoder? encoder = null)
    {
        Name = name;
        _options = options;
        _mapper = mapper;
        _serializer = serializer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<KafkaQueueAdapter>();
        _producer = producer;
        _encoder = encoder;

        if (_options.SecurityProtocol is KafkaSecurityProtocol.Ssl or KafkaSecurityProtocol.SaslSsl
            && string.IsNullOrEmpty(_options.SslCaLocation))
        {
            LogSslNoPathsOsTrustStore(_options.SecurityProtocol);
        }
    }

    public async Task QueueMessageBatchAsync<T>(
        StreamId streamId,
        IEnumerable<T> events,
        StreamSequenceToken token,
        Dictionary<string, object> requestContext)
    {
        if (token is not null)
        {
            throw new ArgumentException(
                "Kafka adapter does not support enqueueing with explicit StreamSequenceToken.",
                nameof(token));
        }

        var batch = new KafkaBatchContainer
        {
            StreamId = streamId,
            Events = events.Cast<object>().ToList(),
            RequestContext = requestContext,
        };

        var bytes = _serializer.SerializeToArray(batch);
        var queueId = _mapper.GetQueueForStream(streamId);
        var topic = KafkaTopicNaming.TopicForQueue(_options, queueId);
        var key = streamId.ToString().AsSpan().ToArray() is { } _
            ? System.Text.Encoding.UTF8.GetBytes(streamId.ToString())
            : Array.Empty<byte>();

        try
        {
            await _producer.ProduceAsync(topic, new Message<byte[], byte[]>
            {
                Key = key,
                Value = bytes,
            }).ConfigureAwait(false);
        }
        catch (ProduceException<byte[], byte[]> ex)
        {
            _logger.LogError(ex, "Failed to publish batch to Kafka topic {Topic}", topic);
            throw;
        }

        if (_encoder is not null)
        {
            var eventsTopic = KafkaTopicNaming.EventsTopicForQueue(_options, queueId);
            foreach (var @event in batch.Events)
            {
                byte[]? encoded;
                try { encoded = await _encoder.EncodeAsync(@event).ConfigureAwait(false); }
                catch (Exception ex) { LogFanoutEncodeFailed(eventsTopic, @event.GetType().Name, ex); continue; }
                if (encoded is null) continue;
                try
                {
                    await _producer.ProduceAsync(eventsTopic,
                        new Message<byte[], byte[]> { Key = key, Value = encoded })
                        .ConfigureAwait(false);
                }
                catch (ProduceException<byte[], byte[]> ex) { LogFanoutProduceFailed(eventsTopic, ex); }
            }
        }
    }

    private static IProducer<byte[], byte[]> BuildProducer(KafkaStreamingOptions options)
    {
        var producerConfig = BuildProducerConfig(options);
        var pb = new ProducerBuilder<byte[], byte[]>(producerConfig);
        if (options.OAuthBearerTokenProvider is not null)
            pb.SetOAuthBearerTokenRefreshHandler(options.OAuthBearerTokenProvider);
        return pb.Build();
    }

    internal static ProducerConfig BuildProducerConfig(KafkaStreamingOptions opts)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = opts.Brokers,
            EnableIdempotence = opts.EnableIdempotence,
            Acks = MapAcks(opts.Acks),
        };
        KafkaClientConfigExtensions.ApplySecurity(config, opts);
        return config;
    }

    private static Acks MapAcks(KafkaAcks acks) => acks switch
    {
        KafkaAcks.All    => Acks.All,
        KafkaAcks.Leader => Acks.Leader,
        KafkaAcks.None   => Acks.None,
        _                => throw new ArgumentOutOfRangeException(nameof(acks), acks, null),
    };

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId) =>
        new KafkaQueueAdapterReceiver(queueId, _options, _serializer, _loggerFactory);

    [LoggerMessage(EventId = 11100, Level = LogLevel.Warning,
        Message = "SecurityProtocol={SecurityProtocol} configured without explicit SSL paths — broker certificate will be validated against the OS trust store.")]
    private partial void LogSslNoPathsOsTrustStore(KafkaSecurityProtocol securityProtocol);

    [LoggerMessage(EventId = 11107, Level = LogLevel.Warning,
        Message = "SR fanout: encoder threw for event type '{EventType}' on topic '{Topic}'.")]
    private partial void LogFanoutEncodeFailed(string topic, string eventType, Exception ex);

    [LoggerMessage(EventId = 11108, Level = LogLevel.Warning,
        Message = "SR fanout: ProduceAsync failed for events topic '{Topic}'.")]
    private partial void LogFanoutProduceFailed(string topic, ProduceException<byte[], byte[]> ex);

    public void Dispose()
    {
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // best-effort flush during teardown
        }
        _producer.Dispose();
    }
}
