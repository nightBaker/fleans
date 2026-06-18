using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

public sealed partial class KafkaQueueAdapterFactory : IQueueAdapterFactory
{
    private readonly string _name;
    private readonly KafkaStreamingOptions _options;
    private readonly Serializer<KafkaBatchContainer> _serializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<KafkaQueueAdapterFactory> _logger;
    private readonly HashRingBasedStreamQueueMapper _mapper;
    private readonly IQueueAdapterCache _cache;
    private readonly IExternalEventEncoder? _encoder;

    public KafkaQueueAdapterFactory(
        string name,
        KafkaStreamingOptions options,
        SimpleQueueCacheOptions cacheOptions,
        Serializer<KafkaBatchContainer> serializer,
        ILoggerFactory loggerFactory,
        IExternalEventEncoder? encoder = null)
    {
        _name = name;
        _options = options;
        _serializer = serializer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<KafkaQueueAdapterFactory>();
        _encoder = encoder;

        _mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = options.QueueCount },
            KafkaTopicNaming.MapperPrefix(options));

        _cache = new SimpleQueueAdapterCache(cacheOptions, name, loggerFactory);
    }

    internal static void ValidateOptions(KafkaStreamingOptions opts)
    {
        if (opts.EnableIdempotence && opts.Acks != KafkaAcks.All)
            throw new InvalidOperationException(
                $"Kafka idempotent producer requires Acks=All (configured: Acks={opts.Acks}). " +
                "Either set Acks=All or set EnableIdempotence=false.");
    }

    public async Task<IQueueAdapter> CreateAdapter()
    {
        ValidateOptions(_options);
        if (_options.Acks != KafkaAcks.All)
            LogAcksNotAll(_options.Acks);
        await EnsureTopicsAsync().ConfigureAwait(false);
        return new KafkaQueueAdapter(_name, _options, _mapper, _serializer, _loggerFactory, _encoder);
    }

    public IQueueAdapterCache GetQueueAdapterCache() => _cache;

    public IStreamQueueMapper GetStreamQueueMapper() => _mapper;

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) =>
        Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler(false));

    /// <summary>
    /// Idempotent client-side topic ensure. Listed missing topics are created with v1 defaults
    /// (NumPartitions/ReplicationFactor from <see cref="KafkaStreamingOptions"/>); broker reports
    /// of <c>TopicAlreadyExists</c> are treated as success (race with a peer silo).
    /// </summary>
    private async Task EnsureTopicsAsync()
    {
        var expected = _encoder is not null
            ? KafkaTopicNaming.AllExpectedTopics(_options)
                .Concat(KafkaTopicNaming.AllExpectedEventTopics(_options))
                .ToArray()
            : KafkaTopicNaming.AllExpectedTopics(_options).ToArray();
        if (expected.Length == 0) return;

        var adminConfig = new AdminClientConfig { BootstrapServers = _options.Brokers };
        KafkaClientConfigExtensions.ApplySecurity(adminConfig, _options);
        if (_options.SecurityProtocol is KafkaSecurityProtocol.Ssl or KafkaSecurityProtocol.SaslSsl
            && string.IsNullOrEmpty(_options.SslCaLocation))
        {
            LogSslNoPathsOsTrustStore(_options.SecurityProtocol);
        }
        var adminBuilder = new AdminClientBuilder(adminConfig);
        if (_options.OAuthBearerTokenProvider is not null)
            adminBuilder.SetOAuthBearerTokenRefreshHandler(_options.OAuthBearerTokenProvider);
        using var admin = adminBuilder.Build();

        HashSet<string> existing;
        short effectiveRf;
        try
        {
            var meta = admin.GetMetadata(_options.AdminTimeout);
            existing = meta.Topics.Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);

            var brokerCount = meta.Brokers.Count;
            if (brokerCount == 0)
                throw new InvalidOperationException(
                    $"Kafka admin reports zero brokers for '{_options.Brokers}'. " +
                    "The cluster is unreachable or misconfigured.");

            effectiveRf = _options.ReplicationFactor;
            if (brokerCount < effectiveRf)
            {
                if (brokerCount == 1)
                    LogSingleBrokerCluster(_options.ReplicationFactor);
                else
                    LogReplicationFactorFallback(_options.ReplicationFactor, brokerCount, brokerCount);
                effectiveRf = (short)brokerCount;
            }
        }
        catch (KafkaException ex)
        {
            LogGetMetadataFailed(ex, _options.Brokers);
            throw;
        }

        var missing = expected
            .Where(t => !existing.Contains(t))
            .Select(t => new TopicSpecification
            {
                Name = t,
                NumPartitions = _options.NumPartitions,
                ReplicationFactor = effectiveRf,
            })
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        try
        {
            await admin.CreateTopicsAsync(missing).ConfigureAwait(false);
            LogTopicsCreated(missing.Count, string.Join(", ", missing.Select(m => m.Name)));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r =>
            r.Error.Code == Confluent.Kafka.ErrorCode.NoError ||
            r.Error.Code == Confluent.Kafka.ErrorCode.TopicAlreadyExists))
        {
            LogTopicsAlreadyExisted(string.Join(", ", missing.Select(m => m.Name)));
        }
    }

    [LoggerMessage(EventId = 11100, Level = LogLevel.Warning,
        Message = "SecurityProtocol={SecurityProtocol} configured without explicit SSL paths — broker certificate will be validated against the OS trust store.")]
    private partial void LogSslNoPathsOsTrustStore(KafkaSecurityProtocol securityProtocol);

    public static KafkaQueueAdapterFactory Create(IServiceProvider services, string name)
    {
        var options = services.GetRequiredService<IOptionsMonitor<KafkaStreamingOptions>>().Get(name);
        var cacheOptions = services.GetRequiredService<IOptionsMonitor<SimpleQueueCacheOptions>>().Get(name);
        var serializer = services.GetRequiredService<Serializer<KafkaBatchContainer>>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var encoder = services.GetService<IExternalEventEncoder>();
        return new KafkaQueueAdapterFactory(name, options, cacheOptions, serializer, loggerFactory, encoder);
    }

    [LoggerMessage(EventId = 11101, Level = LogLevel.Warning,
        Message = "Kafka adapter: Acks={Acks} is not All. Workflow event delivery durability is reduced — this is only safe for non-production workloads.")]
    private partial void LogAcksNotAll(KafkaAcks acks);

    [LoggerMessage(EventId = 11102, Level = LogLevel.Information,
        Message = "Kafka topic-ensure: single-broker cluster detected; using ReplicationFactor=1 instead of configured {Configured}")]
    private partial void LogSingleBrokerCluster(short configured);

    [LoggerMessage(EventId = 11103, Level = LogLevel.Warning,
        Message = "Kafka topic-ensure: configured ReplicationFactor={Configured} exceeds broker count={BrokerCount}; falling back to RF={Effective}. This indicates a partially-degraded or under-provisioned cluster.")]
    private partial void LogReplicationFactorFallback(short configured, int brokerCount, int effective);

    [LoggerMessage(EventId = 11104, Level = LogLevel.Error,
        Message = "Kafka topic-ensure: GetMetadata failed for brokers {Brokers}")]
    private partial void LogGetMetadataFailed(Exception ex, string brokers);

    [LoggerMessage(EventId = 11105, Level = LogLevel.Information,
        Message = "Kafka topic-ensure: created {Count} topic(s): {Topics}")]
    private partial void LogTopicsCreated(int count, string topics);

    [LoggerMessage(EventId = 11106, Level = LogLevel.Information,
        Message = "Kafka topic-ensure: topics already existed (race with peer silo): {Topics}")]
    private partial void LogTopicsAlreadyExisted(string topics);
}
