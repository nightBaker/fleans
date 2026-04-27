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

public sealed class KafkaQueueAdapterFactory : IQueueAdapterFactory
{
    private readonly string _name;
    private readonly KafkaStreamingOptions _options;
    private readonly Serializer<KafkaBatchContainer> _serializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<KafkaQueueAdapterFactory> _logger;
    private readonly HashRingBasedStreamQueueMapper _mapper;
    private readonly IQueueAdapterCache _cache;

    public KafkaQueueAdapterFactory(
        string name,
        KafkaStreamingOptions options,
        SimpleQueueCacheOptions cacheOptions,
        Serializer<KafkaBatchContainer> serializer,
        ILoggerFactory loggerFactory)
    {
        _name = name;
        _options = options;
        _serializer = serializer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<KafkaQueueAdapterFactory>();

        _mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = options.QueueCount },
            KafkaTopicNaming.MapperPrefix(options));

        _cache = new SimpleQueueAdapterCache(cacheOptions, name, loggerFactory);
    }

    public async Task<IQueueAdapter> CreateAdapter()
    {
        await EnsureTopicsAsync().ConfigureAwait(false);
        return new KafkaQueueAdapter(_name, _options, _mapper, _serializer, _loggerFactory);
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
        var expected = KafkaTopicNaming.AllExpectedTopics(_options).ToArray();
        if (expected.Length == 0) return;

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _options.Brokers }).Build();

        HashSet<string> existing;
        try
        {
            var meta = admin.GetMetadata(_options.AdminTimeout);
            existing = meta.Topics.Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
        }
        catch (KafkaException ex)
        {
            _logger.LogError(ex, "Kafka topic-ensure: GetMetadata failed for brokers {Brokers}", _options.Brokers);
            throw;
        }

        var missing = expected
            .Where(t => !existing.Contains(t))
            .Select(t => new TopicSpecification
            {
                Name = t,
                NumPartitions = _options.NumPartitions,
                ReplicationFactor = _options.ReplicationFactor,
            })
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        try
        {
            await admin.CreateTopicsAsync(missing).ConfigureAwait(false);
            _logger.LogInformation(
                "Kafka topic-ensure: created {Count} topic(s): {Topics}",
                missing.Count,
                string.Join(", ", missing.Select(m => m.Name)));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r =>
            r.Error.Code == Confluent.Kafka.ErrorCode.NoError ||
            r.Error.Code == Confluent.Kafka.ErrorCode.TopicAlreadyExists))
        {
            _logger.LogInformation(
                "Kafka topic-ensure: topics already existed (race with peer silo): {Topics}",
                string.Join(", ", missing.Select(m => m.Name)));
        }
    }

    public static KafkaQueueAdapterFactory Create(IServiceProvider services, string name)
    {
        var options = services.GetRequiredService<IOptionsMonitor<KafkaStreamingOptions>>().Get(name);
        var cacheOptions = services.GetRequiredService<IOptionsMonitor<SimpleQueueCacheOptions>>().Get(name);
        var serializer = services.GetRequiredService<Serializer<KafkaBatchContainer>>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        return new KafkaQueueAdapterFactory(name, options, cacheOptions, serializer, loggerFactory);
    }
}
