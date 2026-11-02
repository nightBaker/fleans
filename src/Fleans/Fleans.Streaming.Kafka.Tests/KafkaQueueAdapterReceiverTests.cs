using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka.Tests;

[TestClass]
public class KafkaQueueAdapterReceiverTests
{
    [TestMethod]
    public async Task Failed_committed_offset_fetch_assigns_the_partitions_again()
    {
        // Arrange — the first Consume surfaces a request-level OffsetFetch error, as on a broker still
        // loading the group. librdkafka never retries it for a manually assigned partition (#954).
        var consumer = Substitute.For<IConsumer<byte[], byte[]>>();
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(
            _ => throw new ConsumeException(
                new ConsumeResult<byte[], byte[]>(), new Error(Confluent.Kafka.ErrorCode.NotCoordinatorForGroup)),
            _ => null!);
        var receiver = CreateReceiver(consumer, partitions: [0]);
        await receiver.Initialize(TimeSpan.FromSeconds(1));

        // Act
        await receiver.GetQueueMessagesAsync(maxCount: 10);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (AssignCalls(consumer) < 2 && DateTime.UtcNow < deadline)
        {
            await receiver.GetQueueMessagesAsync(maxCount: 10);
            await Task.Delay(50);
        }

        // Assert — the stuck partition is released and assigned again from the committed offset
        consumer.Received(1).IncrementalUnassign(Arg.Is<IEnumerable<TopicPartition>>(
            p => p.Single().Topic == "fleans-0" && p.Single().Partition.Value == 0));
        Assert.AreEqual(2, AssignCalls(consumer),
            "The receiver must assign the partition again after the failed offset fetch.");
        consumer.Received(2).IncrementalAssign(Arg.Is<IEnumerable<TopicPartitionOffset>>(
            p => p.Single().Partition.Value == 0 && p.Single().Offset == Offset.Stored));
    }

    [TestMethod]
    public async Task Delivered_batches_commit_only_up_to_the_oldest_undelivered_record()
    {
        // Arrange — records 0..3 of partition 0 are read into the Orleans cache; only 0 and 1 are delivered.
        var consumer = ConsumerReturning(Record(0, 0), Record(0, 1), Record(0, 2), Record(0, 3));
        var receiver = CreateReceiver(consumer, partitions: [0]);
        await receiver.Initialize(TimeSpan.FromSeconds(1));
        var batches = await receiver.GetQueueMessagesAsync(maxCount: 10);

        // Act
        await receiver.MessagesDeliveredAsync([batches[0], batches[1]]);

        // Assert — the commit stops at record 2, so a crash now redelivers 2 and 3 instead of losing them
        Assert.HasCount(4, batches);
        consumer.DidNotReceive().Commit();
        Assert.AreEqual("0@2", Committed(consumer).Single());
    }

    [TestMethod]
    public async Task Delivering_a_later_batch_first_does_not_move_the_commit_past_an_earlier_one()
    {
        // Arrange
        var consumer = ConsumerReturning(Record(0, 0), Record(0, 1), Record(0, 2));
        var receiver = CreateReceiver(consumer, partitions: [0]);
        await receiver.Initialize(TimeSpan.FromSeconds(1));
        var batches = await receiver.GetQueueMessagesAsync(maxCount: 10);

        // Act
        await receiver.MessagesDeliveredAsync([batches[2]]);
        await receiver.MessagesDeliveredAsync([batches[0], batches[1]]);

        // Assert — nothing is committed while record 0 is undelivered, then everything is
        CollectionAssert.AreEqual(new[] { "0@3" }, Committed(consumer).ToList());
    }

    [TestMethod]
    public async Task Each_partition_commits_its_own_delivered_offset()
    {
        // Arrange
        var consumer = ConsumerReturning(Record(0, 5), Record(1, 7), Record(0, 6), Record(1, 8));
        var receiver = CreateReceiver(consumer, partitions: [0, 1]);
        await receiver.Initialize(TimeSpan.FromSeconds(1));
        var batches = await receiver.GetQueueMessagesAsync(maxCount: 10);

        // Act — partition 0 is fully delivered, partition 1 only up to offset 7
        await receiver.MessagesDeliveredAsync([batches[0], batches[1], batches[2]]);

        // Assert
        CollectionAssert.AreEquivalent(new[] { "0@7", "1@8" }, Committed(consumer).ToList());
    }

    [TestMethod]
    public async Task A_skipped_record_at_the_end_of_the_topic_is_committed()
    {
        // Arrange — the last record cannot be deserialized (#866), so it never reaches the cache
        var consumer = ConsumerReturning(Record(0, 0), RawRecord(0, 1, [0xFF, 0x00, 0x13]));
        var receiver = CreateReceiver(consumer, partitions: [0]);
        await receiver.Initialize(TimeSpan.FromSeconds(1));
        var batches = await receiver.GetQueueMessagesAsync(maxCount: 10);

        // Act
        await receiver.MessagesDeliveredAsync(batches);

        // Assert — the commit moves past the skipped record once record 0 is delivered
        Assert.HasCount(1, batches);
        CollectionAssert.AreEqual(new[] { "0@2" }, Committed(consumer).ToList());
    }

    [TestMethod]
    public async Task A_poll_of_only_skipped_records_commits_past_them()
    {
        // Arrange — nothing is in flight, so no delivery would ever trigger a commit
        var consumer = ConsumerReturning(RawRecord(0, 4, [0xFF, 0x00, 0x13]));
        var receiver = CreateReceiver(consumer, partitions: [0]);
        await receiver.Initialize(TimeSpan.FromSeconds(1));

        // Act
        var batches = await receiver.GetQueueMessagesAsync(maxCount: 10);

        // Assert
        Assert.IsEmpty(batches);
        CollectionAssert.AreEqual(new[] { "0@5" }, Committed(consumer).ToList());
    }

    [TestMethod]
    public async Task A_failed_commit_is_retried_on_the_next_delivery()
    {
        // Arrange
        var consumer = ConsumerReturning(Record(0, 0), Record(0, 1));
        var calls = 0;
        consumer.When(c => c.Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>())).Do(_ =>
        {
            if (calls++ == 0)
                throw new KafkaException(Confluent.Kafka.ErrorCode.RequestTimedOut);
        });
        var receiver = CreateReceiver(consumer, partitions: [0]);
        await receiver.Initialize(TimeSpan.FromSeconds(1));
        var batches = await receiver.GetQueueMessagesAsync(maxCount: 10);

        // Act
        await receiver.MessagesDeliveredAsync([batches[0]]);
        await receiver.MessagesDeliveredAsync([batches[1]]);

        // Assert
        CollectionAssert.AreEqual(new[] { "0@1", "0@2" }, Committed(consumer).ToList());
    }

    [TestMethod]
    public async Task A_released_assignment_resumes_after_the_last_record_read()
    {
        // Arrange — records 0 and 1 reach the cache undelivered, then Consume fails
        var consumer = Substitute.For<IConsumer<byte[], byte[]>>();
        consumer.GetWatermarkOffsets(Arg.Any<TopicPartition>()).Returns(new WatermarkOffsets(0, 100));
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(
            _ => Record(0, 0),
            _ => Record(0, 1),
            _ => throw new ConsumeException(
                new ConsumeResult<byte[], byte[]>(), new Error(Confluent.Kafka.ErrorCode.NotCoordinatorForGroup)),
            _ => null!);
        var receiver = CreateReceiver(consumer, partitions: [0]);
        await receiver.Initialize(TimeSpan.FromSeconds(1));

        // Act
        var batches = await receiver.GetQueueMessagesAsync(maxCount: 10);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (AssignCalls(consumer) < 2 && DateTime.UtcNow < deadline)
        {
            await receiver.GetQueueMessagesAsync(maxCount: 10);
            await Task.Delay(50);
        }

        // Assert — the partition is assigned again at offset 2, not at the committed offset (0), which
        // would read the two cached records a second time
        Assert.HasCount(2, batches);
        Assert.AreEqual(2, AssignCalls(consumer));
        consumer.Received(1).IncrementalAssign(Arg.Is<IEnumerable<TopicPartitionOffset>>(
            p => p.Single().Partition.Value == 0 && p.Single().Offset.Value == 2));
    }

    [TestMethod]
    public async Task Shutdown_commits_progress_whose_commit_failed()
    {
        // Arrange
        var consumer = ConsumerReturning(Record(0, 0));
        var calls = 0;
        consumer.When(c => c.Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>())).Do(_ =>
        {
            if (calls++ == 0)
                throw new KafkaException(Confluent.Kafka.ErrorCode.RequestTimedOut);
        });
        var receiver = CreateReceiver(consumer, partitions: [0]);
        await receiver.Initialize(TimeSpan.FromSeconds(1));
        var batches = await receiver.GetQueueMessagesAsync(maxCount: 10);
        await receiver.MessagesDeliveredAsync(batches);

        // Act
        await receiver.Shutdown(TimeSpan.FromSeconds(1));

        // Assert — the failed commit is retried before the consumer closes
        CollectionAssert.AreEqual(new[] { "0@1", "0@1" }, Committed(consumer).ToList());
        Received.InOrder(() =>
        {
            consumer.Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
            consumer.Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
            consumer.Close();
        });
    }

    private static readonly Serializer<KafkaBatchContainer> Serializer = new ServiceCollection().AddSerializer()
        .BuildServiceProvider().GetRequiredService<Serializer<KafkaBatchContainer>>();

    private static ConsumeResult<byte[], byte[]> Record(int partition, long offset) =>
        RawRecord(partition, offset, Serializer.SerializeToArray(new KafkaBatchContainer
        {
            StreamId = StreamId.Create("ns", "key"),
            Events = [$"event-{partition}-{offset}"],
        }));

    private static ConsumeResult<byte[], byte[]> RawRecord(int partition, long offset, byte[] value) => new()
    {
        TopicPartitionOffset = new TopicPartitionOffset("fleans-0", partition, offset),
        Message = new Message<byte[], byte[]> { Value = value },
    };

    private static IConsumer<byte[], byte[]> ConsumerReturning(params ConsumeResult<byte[], byte[]>[] records)
    {
        var consumer = Substitute.For<IConsumer<byte[], byte[]>>();
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(records[0], records.Skip(1).Append(null!).ToArray());
        consumer.GetWatermarkOffsets(Arg.Any<TopicPartition>()).Returns(new WatermarkOffsets(0, 100));
        return consumer;
    }

    // Each committed offset as "partition@offset", one entry per partition per Commit call.
    private static IEnumerable<string> Committed(IConsumer<byte[], byte[]> consumer) =>
        consumer.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IConsumer<byte[], byte[]>.Commit)
                && c.GetMethodInfo().GetParameters() is [{ ParameterType: var type }]
                && type == typeof(IEnumerable<TopicPartitionOffset>))
            .SelectMany(c => (IEnumerable<TopicPartitionOffset>)c.GetArguments()[0]!)
            .Select(o => $"{o.Partition.Value}@{o.Offset.Value}");

    private static int AssignCalls(IConsumer<byte[], byte[]> consumer) =>
        consumer.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IConsumer<byte[], byte[]>.IncrementalAssign));

    private static KafkaQueueAdapterReceiver CreateReceiver(IConsumer<byte[], byte[]> consumer, List<int> partitions)
    {
        var options = new KafkaStreamingOptions { TopicPrefix = "fleans", QueueCount = 1 };
        var serializer = new ServiceCollection().AddSerializer().BuildServiceProvider()
            .GetRequiredService<Serializer<KafkaBatchContainer>>();
        return new KafkaQueueAdapterReceiver(
            QueueId.GetQueueId("fleans", 0, 0), options, serializer, NullLoggerFactory.Instance, deadLetters: null,
            buildConsumer: _ => consumer,
            lookUpPartitions: (_, _) => Task.FromResult<IReadOnlyList<int>>(partitions));
    }
}
