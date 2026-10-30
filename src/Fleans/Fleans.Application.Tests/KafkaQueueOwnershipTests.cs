using Confluent.Kafka;
using Fleans.Streaming.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using Testcontainers.Kafka;

namespace Fleans.Application.Tests;

/// <summary>
/// #954: the Orleans queue balancer decides which silo reads a queue; the Kafka receivers must not
/// add a consumer-group balancer of their own. A silo joining the cluster starts agents for a few
/// queues and has them taken away ~100 ms later while membership views converge. With
/// consumer-group <c>Subscribe</c>, a consumer closed mid-join left a pending member that held the
/// whole group — every queue on every silo — in a rebalance until the broker's session timeout
/// (~45 s). That window is too narrow to hit on demand, so the test pins the cause: no group members.
/// Env-gated by <c>FLEANS_KAFKA_TESTS=1</c>.
/// </summary>
[TestClass]
public class KafkaQueueOwnershipTests
{
    private static bool ShouldRun =>
        string.Equals(Environment.GetEnvironmentVariable("FLEANS_KAFKA_TESTS"), "1", StringComparison.Ordinal);

    [TestMethod]
    public async Task Receivers_read_their_queues_without_joining_the_consumer_group()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        // Arrange
        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();
        var (factory, adapter) = await CreateAdapterAsync(kafka.GetBootstrapAddress(), queueCount: 2);
        var queues = AllQueues(factory);
        var receivers = queues.Select(adapter.CreateReceiver).ToList();
        foreach (var receiver in receivers)
            await receiver.Initialize(TimeSpan.FromSeconds(5));
        await adapter.QueueMessageBatchAsync(StreamOn(factory, queues[0]), ["owned"], null!, []);

        // Act
        var received = await PollUntilAsync(receivers[0], "owned", TimeSpan.FromSeconds(30));
        await PollUntilAsync(receivers[1], "none", TimeSpan.FromSeconds(1));

        // Assert — no group members: nothing for a rebalance to wait on when a silo drops a queue
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.GetBootstrapAddress() }).Build();
        var group = (await admin.DescribeConsumerGroupsAsync([new KafkaStreamingOptions().ConsumerGroup]))
            .ConsumerGroupDescriptions.Single();
        foreach (var receiver in receivers)
            await receiver.Shutdown(TimeSpan.FromSeconds(5));
        Assert.IsTrue(received, "The receiver that owns the queue never read its record.");
        Assert.IsEmpty(group.Members, $"Receivers joined consumer group '{group.GroupId}' (state {group.State}).");
    }

    [TestMethod]
    public async Task The_next_owner_of_a_queue_resumes_after_the_committed_offset()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        // Arrange — the first owner reads and commits one record, then gives the queue up
        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();
        var (factory, adapter) = await CreateAdapterAsync(kafka.GetBootstrapAddress(), queueCount: 1);
        var queue = AllQueues(factory)[0];
        var stream = StreamOn(factory, queue);

        var first = adapter.CreateReceiver(queue);
        await first.Initialize(TimeSpan.FromSeconds(5));
        await adapter.QueueMessageBatchAsync(stream, ["delivered"], null!, []);
        Assert.IsTrue(await PollUntilAsync(first, "delivered", TimeSpan.FromSeconds(30)), "The first owner never read its record.");
        await first.Shutdown(TimeSpan.FromSeconds(5));

        // Act
        await adapter.QueueMessageBatchAsync(stream, ["pending"], null!, []);
        var next = adapter.CreateReceiver(queue);
        await next.Initialize(TimeSpan.FromSeconds(5));
        var seen = new List<string>();
        var received = await PollUntilAsync(next, "pending", TimeSpan.FromSeconds(30), seen);
        await next.Shutdown(TimeSpan.FromSeconds(5));

        // Assert
        Assert.IsTrue(received, "The next owner never read the record published after the handoff.");
        CollectionAssert.AreEqual(new[] { "pending" }, seen, "The next owner re-read a committed record.");
    }

    private static async Task<(KafkaQueueAdapterFactory Factory, IQueueAdapter Adapter)> CreateAdapterAsync(
        string brokers, int queueCount)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializer();
        services.Configure<SimpleQueueCacheOptions>("StreamProvider", _ => { });
        var sp = services.BuildServiceProvider();

        var options = new KafkaStreamingOptions
        {
            Brokers = brokers,
            TopicPrefix = $"ownership-{Guid.NewGuid():N}-",
            QueueCount = queueCount,
        };
        var factory = new KafkaQueueAdapterFactory(
            "StreamProvider",
            options,
            sp.GetRequiredService<IOptionsMonitor<SimpleQueueCacheOptions>>().Get("StreamProvider"),
            sp.GetRequiredService<Serializer<KafkaBatchContainer>>(),
            sp.GetRequiredService<ILoggerFactory>());
        return (factory, await factory.CreateAdapter());
    }

    private static List<QueueId> AllQueues(KafkaQueueAdapterFactory factory) =>
        ((IConsistentRingStreamQueueMapper)factory.GetStreamQueueMapper()).GetAllQueues().OrderBy(q => q.ToString()).ToList();

    private static StreamId StreamOn(KafkaQueueAdapterFactory factory, QueueId queue)
    {
        var mapper = factory.GetStreamQueueMapper();
        for (var i = 0; ; i++)
        {
            var stream = StreamId.Create("ownership", i.ToString());
            if (mapper.GetQueueForStream(stream).Equals(queue))
                return stream;
        }
    }

    private static async Task<bool> PollUntilAsync(
        IQueueAdapterReceiver receiver, string expected, TimeSpan timeout, List<string>? seen = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var batches = await receiver.GetQueueMessagesAsync(100);
            var events = batches.SelectMany(b => b.GetEvents<string>().Select(e => e.Item1)).ToList();
            seen?.AddRange(events);
            if (batches.Count > 0)
                await receiver.MessagesDeliveredAsync(batches);
            if (events.Contains(expected))
                return true;
        }
        return false;
    }
}
