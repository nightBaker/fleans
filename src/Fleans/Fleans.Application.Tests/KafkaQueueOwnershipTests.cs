using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Fleans.Streaming.Kafka;
using Orleans.Runtime;
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
        var options = Options(kafka.GetBootstrapAddress(), queueCount: 2);
        var (factory, adapter) = await CreateAdapterAsync(options);
        var queues = AllQueues(factory);
        var receivers = queues.Select(adapter.CreateReceiver).ToList();
        foreach (var receiver in receivers)
            await receiver.Initialize(TimeSpan.FromSeconds(5));
        await adapter.QueueMessageBatchAsync(StreamOn(factory, queues[0]), ["owned"], null!, []);

        // Act
        var received = await PollUntilAsync(receivers[0], HasEvent("owned"), TimeSpan.FromSeconds(30));
        await PollUntilAsync(receivers[1], HasEvent("none"), TimeSpan.FromSeconds(1));

        // Assert — the group exists (the owner committed) but has no members: nothing for a rebalance
        // to wait on when a silo drops a queue
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.GetBootstrapAddress() }).Build();
        var group = (await admin.DescribeConsumerGroupsAsync([options.ConsumerGroup]))
            .ConsumerGroupDescriptions.Single();
        foreach (var receiver in receivers)
            await receiver.Shutdown(TimeSpan.FromSeconds(5));
        Assert.IsTrue(received, "The receiver that owns the queue never read its record.");
        Assert.AreEqual(ConsumerGroupState.Empty, group.State,
            $"Consumer group '{group.GroupId}' has {group.Members.Count} member(s).");
    }

    [TestMethod]
    public async Task The_next_owner_of_a_queue_resumes_after_the_committed_offset()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        // Arrange — the first owner reads and commits one record, then gives the queue up
        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();
        var (factory, adapter) = await CreateAdapterAsync(Options(kafka.GetBootstrapAddress(), queueCount: 1));
        var queue = AllQueues(factory)[0];
        var stream = StreamOn(factory, queue);

        var first = adapter.CreateReceiver(queue);
        await first.Initialize(TimeSpan.FromSeconds(5));
        await adapter.QueueMessageBatchAsync(stream, ["delivered"], null!, []);
        Assert.IsTrue(await PollUntilAsync(first, HasEvent("delivered"), TimeSpan.FromSeconds(30)), "The first owner never read its record.");
        await first.Shutdown(TimeSpan.FromSeconds(5));

        // Act
        await adapter.QueueMessageBatchAsync(stream, ["pending"], null!, []);
        var next = adapter.CreateReceiver(queue);
        await next.Initialize(TimeSpan.FromSeconds(5));
        var seen = new List<string>();
        var received = await PollUntilAsync(next, HasEvent("pending"), TimeSpan.FromSeconds(30), seen);
        await next.Shutdown(TimeSpan.FromSeconds(5));

        // Assert
        Assert.IsTrue(received, "The next owner never read the record published after the handoff.");
        CollectionAssert.AreEqual(new[] { "pending" }, seen, "The next owner re-read a committed record.");
    }

    [TestMethod]
    public async Task Partitions_added_to_a_live_topic_are_read_without_restarting_the_receiver()
    {
        if (!ShouldRun) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        // Arrange — a receiver already reading a one-partition topic
        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();
        var options = Options(kafka.GetBootstrapAddress(), queueCount: 1);
        options.PartitionCheckInterval = TimeSpan.FromSeconds(1);
        var (factory, adapter) = await CreateAdapterAsync(options);
        var queue = AllQueues(factory)[0];
        var receiver = adapter.CreateReceiver(queue);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await adapter.QueueMessageBatchAsync(StreamOn(factory, queue), ["before"], null!, []);
        Assert.IsTrue(await PollUntilAsync(receiver, HasEvent("before"), TimeSpan.FromSeconds(30)), "The receiver never read its first record.");

        // Act — grow the topic, then publish from a fresh producer (an existing one learns of the new
        // partition only at its next metadata refresh) on enough stream keys to hit partition 1
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.GetBootstrapAddress() }).Build())
            await admin.CreatePartitionsAsync([new PartitionsSpecification { Topic = KafkaTopicNaming.TopicForQueue(options, queue), IncreaseTo = 2 }]);
        var (_, freshAdapter) = await CreateAdapterAsync(options);
        for (var i = 0; i < 32; i++)
            await freshAdapter.QueueMessageBatchAsync(StreamId.Create("ownership", $"grown-{i}"), [$"after-{i}"], null!, []);
        var received = await PollUntilAsync(receiver, b => ((KafkaSequenceToken)b.SequenceToken).Partition == 1, TimeSpan.FromSeconds(30));
        await receiver.Shutdown(TimeSpan.FromSeconds(5));

        // Assert
        Assert.IsTrue(received, "The receiver never read a record from the partition added after it started.");
    }

    private static KafkaStreamingOptions Options(string brokers, int queueCount) => new()
    {
        Brokers = brokers,
        ConsumerGroup = $"ownership-{Guid.NewGuid():N}",
        TopicPrefix = $"ownership-{Guid.NewGuid():N}-",
        QueueCount = queueCount,
    };

    private static async Task<(KafkaQueueAdapterFactory Factory, IQueueAdapter Adapter)> CreateAdapterAsync(KafkaStreamingOptions options)
    {
        var factory = KafkaTestAdapterFactory.Create(options);
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

    private static Func<IBatchContainer, bool> HasEvent(string expected) =>
        b => b.GetEvents<string>().Any(e => e.Item1 == expected);

    private static async Task<bool> PollUntilAsync(
        IQueueAdapterReceiver receiver, Func<IBatchContainer, bool> found, TimeSpan timeout, List<string>? seen = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var batches = await receiver.GetQueueMessagesAsync(100);
            seen?.AddRange(batches.SelectMany(b => b.GetEvents<string>().Select(e => e.Item1)));
            if (batches.Count > 0)
                await receiver.MessagesDeliveredAsync(batches);
            if (batches.Any(found))
                return true;
        }
        return false;
    }
}
