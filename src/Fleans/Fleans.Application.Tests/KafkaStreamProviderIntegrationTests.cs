using Fleans.Domain.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Streams;
using Orleans.TestingHost;
using Testcontainers.Kafka;

namespace Fleans.Application.Tests;

/// <summary>
/// Primary acceptance test for issue #372 — at-least-once delivery with consumer-side idempotency.
/// Env-gated by <c>FLEANS_KAFKA_TESTS=1</c> so the default <c>dotnet test</c> stays fast and
/// container-free; CI runs this on a separate matrix leg with Docker available.
/// </summary>
[TestClass]
public class KafkaStreamProviderIntegrationTests
{
    private static bool ShouldRun =>
        string.Equals(Environment.GetEnvironmentVariable("FLEANS_KAFKA_TESTS"), "1", StringComparison.Ordinal);

    private const string ProviderName = "StreamProvider";
    private const string Namespace = "events";

    [TestMethod]
    public async Task Smoke_publish_then_receive_round_trips_via_kafka()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();

        var brokers = kafka.GetBootstrapAddress();
        KafkaTestSiloConfigurator.Brokers = brokers;
        KafkaTestSiloConfigurator.TopicPrefix = $"smoke-{Guid.NewGuid():N}-";

        var cluster = new TestClusterBuilder(1)
            .AddSiloBuilderConfigurator<KafkaTestSiloConfigurator>()
            .Build();
        await cluster.DeployAsync();
        try
        {
            var streamProvider = cluster.Client.GetStreamProvider(ProviderName);
            var streamId = StreamId.Create(Namespace, nameof(ExecuteScriptEvent));
            var publishStream = streamProvider.GetStream<ExecuteScriptEvent>(streamId);

            var received = new List<ExecuteScriptEvent>();
            var subscriptionStream = streamProvider.GetStream<ExecuteScriptEvent>(streamId);
            var sub = await subscriptionStream.SubscribeAsync((evt, _) =>
            {
                lock (received) { received.Add(evt); }
                return Task.CompletedTask;
            });

            await publishStream.OnNextAsync(NewEvent("smoke"));

            await WaitForCountAsync(received, 1, TimeSpan.FromSeconds(30));
            Assert.AreEqual(1, received.Count, "smoke test: expected exactly one delivery");

            await sub.UnsubscribeAsync();
        }
        finally
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Restart_silo_still_delivers_event_at_least_once()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();

        var brokers = kafka.GetBootstrapAddress();
        KafkaTestSiloConfigurator.Brokers = brokers;
        KafkaTestSiloConfigurator.TopicPrefix = $"restart-{Guid.NewGuid():N}-";

        var streamId = StreamId.Create(Namespace, nameof(ExecuteScriptEvent));

        // Phase 1: publish into Kafka, immediately tear the silo down WITHOUT giving the
        // consumer a chance to commit offsets. The event remains in the topic.
        var clusterPub = new TestClusterBuilder(1)
            .AddSiloBuilderConfigurator<KafkaTestSiloConfigurator>()
            .Build();
        await clusterPub.DeployAsync();
        var publishProvider = clusterPub.Client.GetStreamProvider(ProviderName);
        var publishStream = publishProvider.GetStream<ExecuteScriptEvent>(streamId);
        await publishStream.OnNextAsync(NewEvent("restart"));
        await clusterPub.StopAllSilosAsync();
        await clusterPub.DisposeAsync();

        // Phase 2: bring up a fresh silo against the same broker + topic prefix and subscribe.
        // The published event must be redelivered (at-least-once contract).
        var clusterSub = new TestClusterBuilder(1)
            .AddSiloBuilderConfigurator<KafkaTestSiloConfigurator>()
            .Build();
        await clusterSub.DeployAsync();
        try
        {
            var subProvider = clusterSub.Client.GetStreamProvider(ProviderName);
            var subStream = subProvider.GetStream<ExecuteScriptEvent>(streamId);

            var received = new List<ExecuteScriptEvent>();
            var sub = await subStream.SubscribeAsync((evt, _) =>
            {
                lock (received) { received.Add(evt); }
                return Task.CompletedTask;
            });

            await WaitForCountAsync(received, 1, TimeSpan.FromSeconds(60));
            Assert.IsTrue(received.Count >= 1, "at-least-once: expected the published event to redeliver after restart");

            await sub.UnsubscribeAsync();
        }
        finally
        {
            await clusterSub.StopAllSilosAsync();
            await clusterSub.DisposeAsync();
        }
    }

    private static ExecuteScriptEvent NewEvent(string activityId) => new(
        WorkflowInstanceId: Guid.NewGuid(),
        WorkflowId: "kafka-test",
        ProcessDefinitionId: null,
        ActivityInstanceId: Guid.NewGuid(),
        ActivityId: activityId,
        Script: "_context.x = 1",
        ScriptFormat: "csharp",
        VariablesId: Guid.NewGuid());

    private static async Task WaitForCountAsync<T>(List<T> bag, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (bag)
            {
                if (bag.Count >= expected) return;
            }
            await Task.Delay(200);
        }
    }

    private sealed class KafkaTestSiloConfigurator : ISiloConfigurator
    {
        public static string Brokers { get; set; } = string.Empty;
        public static string TopicPrefix { get; set; } = "fleans-test-";

        public void Configure(ISiloBuilder siloBuilder)
        {
            var dict = new Dictionary<string, string?>
            {
                ["Fleans:Streaming:Provider"] = "kafka",
                ["Fleans:Streaming:Kafka:Brokers"] = Brokers,
                ["Fleans:Streaming:Kafka:ConsumerGroup"] = $"fleans-test-{Guid.NewGuid():N}",
                ["Fleans:Streaming:Kafka:TopicPrefix"] = TopicPrefix,
                ["Fleans:Streaming:Kafka:QueueCount"] = "1",
            };
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
            siloBuilder.AddFleanStreaming(cfg);
            siloBuilder.AddMemoryGrainStorage("PubSubStore");
        }
    }
}
