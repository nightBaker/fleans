using Orleans.Streams;

namespace Fleans.Streaming.Kafka.Tests;

[TestClass]
public class KafkaSequenceTokenTests
{
    [TestMethod]
    public void CompareTo_OrdersByReceiveOrder_NotOffset_AcrossPartitions()
    {
        // p0@100 was consumed before p1@3 — offsets alone would order them the other way round.
        var first = new KafkaSequenceToken(offset: 100, eventIndex: 0, partition: 0, receiveOrder: 1);
        var second = new KafkaSequenceToken(offset: 3, eventIndex: 0, partition: 1, receiveOrder: 2);

        Assert.IsTrue(first.Older(second));
        Assert.IsTrue(second.Newer(first));
    }

    [TestMethod]
    public void SamePartitionAndOffset_DifferentPartition_AreNotEqual()
    {
        var p0 = new KafkaSequenceToken(5, 0, partition: 0);
        var p1 = new KafkaSequenceToken(5, 0, partition: 1);

        Assert.IsFalse(p0.Equals(p1));
        Assert.AreNotEqual(0, p0.CompareTo(p1));
    }

    [TestMethod]
    public void BatchContainer_EventTokens_CarryPartitionAndReceiveOrder()
    {
        // Arrange
        var container = new KafkaBatchContainer
        {
            Events = ["a", "b"],
            Token = new KafkaSequenceToken(offset: 5, eventIndex: 0, partition: 2, receiveOrder: 42),
        };

        // Act
        var tokens = container.GetEvents<string>().Select(t => (KafkaSequenceToken)t.Item2).ToList();

        // Assert — a failure handler given an event token must locate the same record.
        Assert.HasCount(2, tokens);
        foreach (var token in tokens)
        {
            Assert.AreEqual(2, token.Partition);
            Assert.AreEqual(5, token.Offset);
            Assert.AreEqual(42, token.ReceiveOrder);
        }
        Assert.AreEqual(0, tokens[0].EventIndex);
        Assert.AreEqual(1, tokens[1].EventIndex);
        Assert.IsTrue(container.SequenceToken.Equals(tokens[0]));
        Assert.IsTrue(tokens[0].Older(tokens[1]));
    }
}
