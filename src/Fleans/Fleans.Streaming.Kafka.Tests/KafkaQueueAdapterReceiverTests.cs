using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
