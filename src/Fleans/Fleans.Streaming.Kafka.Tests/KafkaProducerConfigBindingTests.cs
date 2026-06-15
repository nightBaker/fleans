using Confluent.Kafka;

namespace Fleans.Streaming.Kafka.Tests;

/// <summary>
/// Verifies that <see cref="KafkaStreamingOptions"/> durability properties bind correctly
/// to the <see cref="ProducerConfig"/> built by <see cref="KafkaQueueAdapter.BuildProducerConfig"/>
/// and that <see cref="KafkaQueueAdapterFactory.ValidateOptions"/> enforces the
/// idempotence × acks constraint.
/// </summary>
[TestClass]
public class KafkaProducerConfigBindingTests
{
    [TestMethod]
    public void Default_options_producer_config_satisfies_idempotence_preconditions()
    {
        var opts = new KafkaStreamingOptions();
        var config = KafkaQueueAdapter.BuildProducerConfig(opts);

        Assert.IsTrue(config.EnableIdempotence == true,
            "Default EnableIdempotence must be true.");
        Assert.AreEqual(Acks.All, config.Acks,
            "Default Acks must be All (required for idempotent producer).");
        Assert.IsNull(config.MaxInFlight,
            "MaxInFlight must not be explicitly set — library default (5) is safe for idempotence.");
        Assert.IsNull(config.MessageSendMaxRetries,
            "MessageSendMaxRetries must not be explicitly set to 0 — library default is safe.");
    }

    [TestMethod]
    public void EnableIdempotence_with_Acks_Leader_throws()
    {
        var opts = new KafkaStreamingOptions
        {
            EnableIdempotence = true,
            Acks = KafkaAcks.Leader,
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaQueueAdapterFactory.ValidateOptions(opts),
            "Idempotent producer requires Acks=All; Leader should throw.");
    }

    [TestMethod]
    public void EnableIdempotence_with_Acks_None_throws()
    {
        var opts = new KafkaStreamingOptions
        {
            EnableIdempotence = true,
            Acks = KafkaAcks.None,
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaQueueAdapterFactory.ValidateOptions(opts),
            "Idempotent producer requires Acks=All; None should throw.");
    }

    [TestMethod]
    public void EnableIdempotence_false_with_Acks_Leader_does_not_throw()
    {
        var opts = new KafkaStreamingOptions
        {
            EnableIdempotence = false,
            Acks = KafkaAcks.Leader,
        };

        // Must not throw — this is a valid (if lower-durability) configuration.
        KafkaQueueAdapterFactory.ValidateOptions(opts);
    }

    [TestMethod]
    public void MapAcks_All_roundtrip()
    {
        var opts = new KafkaStreamingOptions { Acks = KafkaAcks.All };
        var config = KafkaQueueAdapter.BuildProducerConfig(opts);
        Assert.AreEqual(Acks.All, config.Acks);
    }

    [TestMethod]
    public void MapAcks_Leader_roundtrip()
    {
        var opts = new KafkaStreamingOptions { EnableIdempotence = false, Acks = KafkaAcks.Leader };
        var config = KafkaQueueAdapter.BuildProducerConfig(opts);
        Assert.AreEqual(Acks.Leader, config.Acks);
    }

    [TestMethod]
    public void MapAcks_None_roundtrip()
    {
        var opts = new KafkaStreamingOptions { EnableIdempotence = false, Acks = KafkaAcks.None };
        var config = KafkaQueueAdapter.BuildProducerConfig(opts);
        Assert.AreEqual(Acks.None, config.Acks);
    }
}
