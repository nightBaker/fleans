using Fleans.Streaming.Kafka;

namespace Fleans.Infrastructure.Tests;

/// <summary>
/// Verifies the production-preset math (issue #684). Tests target the internal
/// <c>KafkaProductionPreset</c> helper directly — no host required.
/// </summary>
[TestClass]
public class KafkaProductionPresetTests
{
    // --- QueueCount ---

    [TestMethod]
    public void QueueCount_returns_8_when_cores_below_floor()
    {
        Assert.AreEqual(8, KafkaProductionPreset.QueueCount(1));
        Assert.AreEqual(8, KafkaProductionPreset.QueueCount(4));
        Assert.AreEqual(8, KafkaProductionPreset.QueueCount(7));
    }

    [TestMethod]
    public void QueueCount_returns_8_at_exactly_8_cores()
    {
        Assert.AreEqual(8, KafkaProductionPreset.QueueCount(8));
    }

    [TestMethod]
    public void QueueCount_scales_linearly_above_8_cores()
    {
        Assert.AreEqual(16, KafkaProductionPreset.QueueCount(16));
        Assert.AreEqual(32, KafkaProductionPreset.QueueCount(32));
        Assert.AreEqual(96, KafkaProductionPreset.QueueCount(96));
    }

    // --- NumPartitions ---

    [TestMethod]
    public void NumPartitions_returns_1_at_exactly_1_core()
    {
        Assert.AreEqual(1, KafkaProductionPreset.NumPartitions(1));
    }

    [TestMethod]
    public void NumPartitions_scales_linearly_above_1_core()
    {
        Assert.AreEqual(8,  KafkaProductionPreset.NumPartitions(8));
        Assert.AreEqual(16, KafkaProductionPreset.NumPartitions(16));
    }

    // --- Regression guard: defaults must be unchanged when preset is NOT applied ---

    [TestMethod]
    public void Default_QueueCount_is_8_without_preset()
    {
        var options = new KafkaStreamingOptions();

        Assert.AreEqual(8, options.QueueCount,
            "Default QueueCount must stay 8 regardless of preset — WithProductionDefaults is opt-in.");
    }

    [TestMethod]
    public void Default_NumPartitions_is_1_without_preset()
    {
        var options = new KafkaStreamingOptions();

        Assert.AreEqual(1, options.NumPartitions,
            "Default NumPartitions must stay 1 regardless of preset — WithProductionDefaults is opt-in.");
    }
}
