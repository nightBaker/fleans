using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// Covers <see cref="FleanStreamingExtensions.ReadRedisTotalQueueCount"/> — the validation
/// surface for the Redis Orleans-parallelism knob (issue #567 v3 design).
/// </summary>
[TestClass]
public class RedisStreamingOptionsTests
{
    [TestMethod]
    public void Returns_default_8_when_TotalQueueCount_absent()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var count = FleanStreamingExtensions.ReadRedisTotalQueueCount(cfg);

        Assert.AreEqual(8, count);
    }

    [TestMethod]
    public void Reads_TotalQueueCount_from_configuration()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fleans:Streaming:Redis:TotalQueueCount"] = "16",
            })
            .Build();

        var count = FleanStreamingExtensions.ReadRedisTotalQueueCount(cfg);

        Assert.AreEqual(16, count);
    }

    [TestMethod]
    public void Throws_when_TotalQueueCount_is_non_integer()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fleans:Streaming:Redis:TotalQueueCount"] = "eight",
            })
            .Build();

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => FleanStreamingExtensions.ReadRedisTotalQueueCount(cfg));
        StringAssert.Contains(ex.Message, "must be an integer");
        StringAssert.Contains(ex.Message, "'eight'");
        StringAssert.Contains(ex.Message, "Fleans__Streaming__Redis__TotalQueueCount");
    }

    [TestMethod]
    public void Throws_when_TotalQueueCount_is_zero()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fleans:Streaming:Redis:TotalQueueCount"] = "0",
            })
            .Build();

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => FleanStreamingExtensions.ReadRedisTotalQueueCount(cfg));
        StringAssert.Contains(ex.Message, ">= 1");
        StringAssert.Contains(ex.Message, "Fleans__Streaming__Redis__TotalQueueCount");
    }

    [TestMethod]
    public void Throws_when_TotalQueueCount_is_negative()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fleans:Streaming:Redis:TotalQueueCount"] = "-3",
            })
            .Build();

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => FleanStreamingExtensions.ReadRedisTotalQueueCount(cfg));
        StringAssert.Contains(ex.Message, ">= 1");
        StringAssert.Contains(ex.Message, "(got -3)");
    }
}
