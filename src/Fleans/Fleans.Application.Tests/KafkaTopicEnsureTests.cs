using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Fleans.Streaming.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;
using Testcontainers.Kafka;

namespace Fleans.Application.Tests;

/// <summary>
/// Verifies the client-side topic ensure logic in <see cref="KafkaQueueAdapterFactory"/>.
/// Env-gated by <c>FLEANS_KAFKA_TESTS=1</c> so the default <c>dotnet test</c> remains fast and
/// container-free; CI runs this on a separate matrix leg with Docker.
/// </summary>
[TestClass]
public class KafkaTopicEnsureTests
{
    private static bool ShouldRun =>
        string.Equals(Environment.GetEnvironmentVariable("FLEANS_KAFKA_TESTS"), "1", StringComparison.Ordinal);

    [TestMethod]
    public async Task Empty_broker_topics_are_created_on_adapter_init()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();

        var brokers = kafka.GetBootstrapAddress();
        var options = new KafkaStreamingOptions
        {
            Brokers = brokers,
            ConsumerGroup = "test-empty",
            TopicPrefix = "ensure-empty-",
            QueueCount = 3,
        };

        var factory = CreateFactory(options);
        await factory.CreateAdapter();

        var existing = await ListTopicsAsync(brokers);
        foreach (var t in KafkaTopicNaming.AllExpectedTopics(options))
        {
            Assert.IsTrue(existing.Contains(t), $"expected topic '{t}' to be created on broker");
        }
    }

    [TestMethod]
    public async Task Pre_existing_topics_are_a_no_op()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();

        var brokers = kafka.GetBootstrapAddress();
        var options = new KafkaStreamingOptions
        {
            Brokers = brokers,
            ConsumerGroup = "test-preexisting",
            TopicPrefix = "ensure-preexisting-",
            QueueCount = 2,
        };

        // Pre-create the topics manually before the factory runs its ensure step.
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = brokers }).Build())
        {
            var specs = KafkaTopicNaming.AllExpectedTopics(options)
                .Select(t => new TopicSpecification { Name = t, NumPartitions = 1, ReplicationFactor = 1 })
                .ToList();
            await admin.CreateTopicsAsync(specs);
        }

        var factory = CreateFactory(options);
        // Should NOT throw — the factory must treat pre-existing topics as success.
        await factory.CreateAdapter();

        var existing = await ListTopicsAsync(brokers);
        foreach (var t in KafkaTopicNaming.AllExpectedTopics(options))
        {
            Assert.IsTrue(existing.Contains(t));
        }
    }

    private static KafkaQueueAdapterFactory CreateFactory(KafkaStreamingOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializer();
        services.Configure<KafkaStreamingOptions>("StreamProvider", o =>
        {
            o.Brokers = options.Brokers;
            o.ConsumerGroup = options.ConsumerGroup;
            o.TopicPrefix = options.TopicPrefix;
            o.QueueCount = options.QueueCount;
            o.NumPartitions = options.NumPartitions;
            o.ReplicationFactor = options.ReplicationFactor;
        });
        services.Configure<SimpleQueueCacheOptions>("StreamProvider", _ => { });
        var sp = services.BuildServiceProvider();

        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var serializer = sp.GetRequiredService<Serializer<KafkaBatchContainer>>();
        var cacheOpts = sp.GetRequiredService<IOptionsMonitor<SimpleQueueCacheOptions>>().Get("StreamProvider");
        var opts = sp.GetRequiredService<IOptionsMonitor<KafkaStreamingOptions>>().Get("StreamProvider");
        return new KafkaQueueAdapterFactory("StreamProvider", opts, cacheOpts, serializer, loggerFactory);
    }

    private static Task<HashSet<string>> ListTopicsAsync(string brokers)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = brokers }).Build();
        var meta = admin.GetMetadata(TimeSpan.FromSeconds(10));
        return Task.FromResult(meta.Topics.Select(t => t.Topic).ToHashSet(StringComparer.Ordinal));
    }
}
