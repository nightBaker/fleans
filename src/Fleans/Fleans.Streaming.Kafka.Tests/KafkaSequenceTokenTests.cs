using Orleans.Streams;

namespace Fleans.Streaming.Kafka.Tests;

[TestClass]
public class KafkaSequenceTokenTests
{
    [TestMethod]
    public void SameRecord_YieldsEqualTokens_IndependentOfProcess()
    {
        // Orleans keeps tokens across queue rebalances and silo restarts, so a token must be built
        // only from Kafka coordinates — never from process-local state.
        var a = new KafkaSequenceToken(offset: 5, eventIndex: 1, partition: 2);
        var b = new KafkaSequenceToken(offset: 5, eventIndex: 1, partition: 2);

        Assert.IsTrue(a.Equals(b));
        Assert.AreEqual(0, a.CompareTo(b));
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void SameOffset_DifferentPartition_AreNotEqual()
    {
        var p0 = new KafkaSequenceToken(5, 0, partition: 0);
        var p1 = new KafkaSequenceToken(5, 0, partition: 1);

        Assert.IsFalse(p0.Equals(p1));
        Assert.AreNotEqual(0, p0.CompareTo(p1));
    }

    [TestMethod]
    public void BatchContainer_EventTokens_CarryPartitionAndOffset()
    {
        // Arrange
        var container = new KafkaBatchContainer
        {
            Events = ["a", "b"],
            Token = new KafkaSequenceToken(offset: 5, eventIndex: 0, partition: 2),
        };

        // Act
        var tokens = container.GetEvents<string>().Select(t => (KafkaSequenceToken)t.Item2).ToList();

        // Assert — a failure handler given an event token must locate the same record.
        Assert.HasCount(2, tokens);
        foreach (var token in tokens)
        {
            Assert.AreEqual(2, token.Partition);
            Assert.AreEqual(5, token.Offset);
        }
        Assert.AreEqual(0, tokens[0].EventIndex);
        Assert.AreEqual(1, tokens[1].EventIndex);
        Assert.IsTrue(container.SequenceToken.Equals(tokens[0]));
        Assert.IsTrue(tokens[0].Older(tokens[1]));
    }
}
