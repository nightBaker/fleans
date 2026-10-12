using System.Text;
using Confluent.Kafka;
using Fleans.Application.Abstractions.Events;
using Fleans.Domain.Events;
using Fleans.Streaming.Kafka;
using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streams;
using Orleans.TestingHost;
using Testcontainers.Kafka;

namespace Fleans.Application.Tests;

/// <summary>
/// Acceptance tests for issue #686 — records whose subscriber keeps failing end up in the <c>-dlq</c> topic
/// with diagnostic headers; records that cannot be deserialized are logged and skipped, never dead-lettered.
/// Env-gated by <c>FLEANS_KAFKA_TESTS=1</c>.
/// </summary>
[TestClass]
public class KafkaDlqIntegrationTests
{
    private static bool ShouldRun =>
        string.Equals(Environment.GetEnvironmentVariable("FLEANS_KAFKA_TESTS"), "1", StringComparison.Ordinal);

    private const string ProviderName = "StreamProvider";
    private const string Namespace = WorkflowEventStreams.ExecuteScriptStreamNamespace;

    [TestMethod]
    public async Task Failing_subscriber_record_is_dead_lettered_and_stream_keeps_flowing()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        // Arrange
        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();
        var (sourceTopic, dlqTopic) = UseFreshTopics(kafka.GetBootstrapAddress());

        var cluster = BuildCluster();
        await cluster.DeployAsync();
        try
        {
            var stream = cluster.Client.GetStreamProvider(ProviderName)
                .GetStream<ExecuteScriptEvent>(StreamId.Create(Namespace, nameof(ExecuteScriptEvent)));

            var poisonAttempts = 0;
            var healthy = new List<string>();
            var sub = await stream.SubscribeAsync((evt, _) =>
            {
                if (evt.ActivityId == "poison")
                {
                    Interlocked.Increment(ref poisonAttempts);
                    throw new InvalidOperationException("poison");
                }
                lock (healthy) { healthy.Add(evt.ActivityId); }
                return Task.CompletedTask;
            });

            // Act
            await stream.OnNextAsync(NewEvent("poison"));
            await stream.OnNextAsync(NewEvent("healthy"));
            var dlqMessage = ConsumeOne(DlqSiloConfigurator.Brokers, dlqTopic, TimeSpan.FromSeconds(90));
            await WaitUntilAsync(() => { lock (healthy) { return healthy.Count > 0; } }, TimeSpan.FromSeconds(30));

            // Assert
            Assert.IsNotNull(dlqMessage, $"expected the poison record in '{dlqTopic}'");
            Assert.IsTrue(poisonAttempts > 1, $"Orleans should retry before giving up (attempts={poisonAttempts})");
            Assert.AreNotEqual(0, dlqMessage.Value.Length, "DLQ payload must carry the original raw bytes");
            Assert.AreEqual(sourceTopic, Header(dlqMessage, "x-fleans-original-topic"));
            Assert.AreEqual("0", Header(dlqMessage, "x-fleans-original-partition"));
            Assert.AreEqual("0", Header(dlqMessage, "x-fleans-original-offset"));
            Assert.AreEqual("delivery-failed", Header(dlqMessage, "x-fleans-failure-reason"));
            Assert.IsNotNull(Header(dlqMessage, "x-fleans-stream-id"));
            Assert.IsNotNull(Header(dlqMessage, "x-fleans-subscription-id"), "replay must be able to target only the failing subscriber");
            Assert.IsNotNull(Header(dlqMessage, "x-fleans-failure-time-utc"));
            lock (healthy) { CollectionAssert.AreEqual(new[] { "healthy" }, healthy, "the record after the poison one must still be delivered"); }

            await sub.UnsubscribeAsync();
        }
        finally
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Undeserializable_record_is_skipped_not_dead_lettered_and_stream_keeps_flowing()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        // Arrange
        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();
        var (sourceTopic, dlqTopic) = UseFreshTopics(kafka.GetBootstrapAddress());

        var cluster = BuildCluster();
        await cluster.DeployAsync();
        try
        {
            var stream = cluster.Client.GetStreamProvider(ProviderName)
                .GetStream<ExecuteScriptEvent>(StreamId.Create(Namespace, nameof(ExecuteScriptEvent)));
            var healthy = new List<string>();
            var sub = await stream.SubscribeAsync((evt, _) =>
            {
                lock (healthy) { healthy.Add(evt.ActivityId); }
                return Task.CompletedTask;
            });

            // Act — bytes the receiver cannot deserialize, straight into the source topic, then a healthy event.
            using (var producer = new ProducerBuilder<Null, byte[]>(new ProducerConfig { BootstrapServers = DlqSiloConfigurator.Brokers }).Build())
            {
                await producer.ProduceAsync(sourceTopic, new Message<Null, byte[]> { Value = Encoding.UTF8.GetBytes("not an Orleans-serialized batch") });
            }
            await stream.OnNextAsync(NewEvent("healthy"));
            await WaitUntilAsync(() => { lock (healthy) { return healthy.Count > 0; } }, TimeSpan.FromSeconds(30));
            var dlqMessage = ConsumeOne(DlqSiloConfigurator.Brokers, dlqTopic, TimeSpan.FromSeconds(5));

            // Assert — log-only by design: deserialization failures never reach the DLQ.
            lock (healthy) { CollectionAssert.AreEqual(new[] { "healthy" }, healthy, "the record after the garbage one must still be delivered"); }
            Assert.IsNull(dlqMessage, $"undeserializable records must not be dead-lettered, but '{dlqTopic}' has a record");

            await sub.UnsubscribeAsync();
        }
        finally
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    private static (string SourceTopic, string DlqTopic) UseFreshTopics(string brokers)
    {
        DlqSiloConfigurator.Brokers = brokers;
        DlqSiloConfigurator.TopicPrefix = $"dlq-{Guid.NewGuid():N}-";
        var sourceTopic = $"{DlqSiloConfigurator.TopicPrefix.TrimEnd('-')}-0";
        return (sourceTopic, sourceTopic + "-dlq");
    }

    private static TestCluster BuildCluster() => new TestClusterBuilder(1)
        .AddSiloBuilderConfigurator<DlqSiloConfigurator>()
        .AddClientBuilderConfigurator<DlqClientConfigurator>()
        .Build();

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(200);
    }

    private static Message<Ignore, byte[]>? ConsumeOne(string brokers, string topic, TimeSpan timeout)
    {
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
        {
            BootstrapServers = brokers,
            GroupId = $"dlq-assert-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message is not null) return result.Message;
        }
        return null;
    }

    private static string? Header(Message<Ignore, byte[]> message, string key) =>
        message.Headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    private static ExecuteScriptEvent NewEvent(string activityId) => new(
        WorkflowInstanceId: Guid.NewGuid(),
        WorkflowId: "kafka-dlq-test",
        ProcessDefinitionId: null,
        ActivityInstanceId: Guid.NewGuid(),
        ActivityId: activityId,
        Script: "_context.x = 1",
        ScriptFormat: "csharp",
        VariablesId: Guid.NewGuid());

    private sealed class DlqSiloConfigurator : ISiloConfigurator
    {
        public static string Brokers { get; set; } = string.Empty;
        public static string TopicPrefix { get; set; } = "fleans-dlq-test-";

        public void Configure(ISiloBuilder siloBuilder)
        {
            var dict = new Dictionary<string, string?>
            {
                ["Brokers"] = Brokers,
                ["ConsumerGroup"] = $"fleans-dlq-test-{Guid.NewGuid():N}",
                ["TopicPrefix"] = TopicPrefix,
                ["QueueCount"] = "1",
                ["EnableDeadLetterQueue"] = "true",
                // Shortens Orleans' per-record retry window (default 1 minute) — also exercises the option wiring.
                ["DeliveryRetryWindow"] = "00:00:05",
            };
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
            siloBuilder.AddKafkaStreams(ProviderName, cfg);
            siloBuilder.AddMemoryGrainStorage("PubSubStore");
        }
    }

    private sealed class DlqClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.AddPersistentStreams(
                ProviderName,
                KafkaQueueAdapterFactory.Create,
                b =>
                {
                    b.Configure<KafkaStreamingOptions>(o => o.Configure(opt =>
                    {
                        opt.Brokers = DlqSiloConfigurator.Brokers;
                        opt.TopicPrefix = DlqSiloConfigurator.TopicPrefix;
                        opt.ConsumerGroup = $"fleans-dlq-test-client-{Guid.NewGuid():N}";
                        opt.QueueCount = 1;
                    }));
                    b.ConfigureStreamPubSub(StreamPubSubType.ExplicitGrainBasedAndImplicit);
                });
        }
    }
}
