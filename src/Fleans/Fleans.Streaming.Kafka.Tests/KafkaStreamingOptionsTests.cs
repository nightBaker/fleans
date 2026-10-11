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
    public void ReplicationFactor_default_is_3()
    {
        var options = new KafkaStreamingOptions();

        Assert.AreEqual((short)3, options.ReplicationFactor,
            "Default RF=3 survives one broker loss; factory falls back to broker count when fewer brokers available.");
    }

    [TestMethod]
    public void EnableIdempotence_default_is_true()
    {
        var options = new KafkaStreamingOptions();

        Assert.IsTrue(options.EnableIdempotence,
            "Idempotent producer is on by default for exactly-once produce semantics.");
    }

    [TestMethod]
    public void Acks_default_is_All()
    {
        var options = new KafkaStreamingOptions();

        Assert.AreEqual(KafkaAcks.All, options.Acks,
            "Acks=All is required for the default idempotent producer and provides maximum durability.");
    }
}
