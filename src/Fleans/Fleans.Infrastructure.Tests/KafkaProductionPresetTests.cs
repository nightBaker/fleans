using Fleans.Streaming.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;

namespace Fleans.Infrastructure.Tests;

/// <summary>
/// Verifies the production-preset math (issue #684). Tests target the internal
/// <c>KafkaProductionPreset</c> helper directly — no host required.
/// </summary>
[TestClass]
public class KafkaProductionPresetTests
{
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

    // --- QueueCount stays config-driven (#699) ---

    [TestMethod]
    public void Preset_does_not_override_QueueCount()
    {
        var builder = new FakeSiloBuilder();
        builder.Services.AddLogging();
        builder.Services.Configure<KafkaStreamingOptions>("kafka", o => o.QueueCount = 3);

        builder.WithProductionDefaults("kafka");

        var options = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<KafkaStreamingOptions>>().Get("kafka");
        Assert.AreEqual(3, options.QueueCount,
            "QueueCount is a cluster-wide hash-ring value; deriving it from the host's core count lets a mixed-core fleet misroute streams.");
        Assert.AreEqual(KafkaProductionPreset.NumPartitions(Environment.ProcessorCount), options.NumPartitions);
    }

    private sealed class FakeSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();

        public ISiloBuilder ConfigureServices(Action<IServiceCollection> configureDelegate)
        {
            configureDelegate(Services);
            return this;
        }
    }
}
