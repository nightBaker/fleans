namespace Fleans.Streaming.Kafka.Tests;

/// <summary>
/// Pins the Kafka streaming defaults (issue #567 v3 design): <c>QueueCount=8</c> matches the
/// Orleans-parallelism baseline of peer providers; <c>NumPartitions=1</c> stays intentionally
/// low because it is an independent Kafka-side topology knob, not a parallelism multiplier on
/// top of <c>QueueCount</c>.
/// </summary>
[TestClass]
public class KafkaStreamingOptionsTests
{
    [TestMethod]
    public void QueueCount_default_is_8()
    {
        var options = new KafkaStreamingOptions();

        Assert.AreEqual(8, options.QueueCount,
            "Kafka QueueCount default should match peer providers' Orleans-parallelism baseline.");
    }

    [TestMethod]
    public void NumPartitions_default_is_1()
    {
        var options = new KafkaStreamingOptions();

        Assert.AreEqual(1, options.NumPartitions,
            "Kafka NumPartitions stays at 1 — it is a Kafka-side topology knob, not an " +
            "Orleans consumer-parallelism multiplier. Operators opt into >1 deliberately.");
    }

    [TestMethod]
    public void EnableDeadLetterQueue_default_is_false()
    {
        var options = new KafkaStreamingOptions();

        Assert.IsFalse(options.EnableDeadLetterQueue,
            "DLQ is opt-in — enabling it by default would create -dlq topics for every " +
            "existing deployment on upgrade, which is a breaking change.");
    }

    [TestMethod]
    public void MaxConsumerRetries_default_is_3()
    {
        var options = new KafkaStreamingOptions();

        Assert.AreEqual(3, options.MaxConsumerRetries,
            "3 retries before DLQ routing is the baseline; operators tune per-stream.");
    }

    [TestMethod]
    public void DeadLetterTopicSuffix_default_is_dash_dlq()
    {
        var options = new KafkaStreamingOptions();

        Assert.AreEqual("-dlq", options.DeadLetterTopicSuffix,
            "'-dlq' suffix is the Kafka community convention for dead-letter topics.");
    }
}
