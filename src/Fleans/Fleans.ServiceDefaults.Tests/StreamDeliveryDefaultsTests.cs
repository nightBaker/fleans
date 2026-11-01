using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// #850: Orleans' 1-minute <c>MaxEventDeliveryTime</c> skipped events queued behind a long
/// custom-task run, and Redis' 1000-entry cap trimmed unread entries. Every provider now gets a
/// 6-minute window (<c>Fleans:Streaming:MaxEventDeliveryTime</c>) and Redis keeps 100k entries.
/// </summary>
[TestClass]
public class StreamDeliveryDefaultsTests
{
    private static ServiceProvider Build(string provider, Dictionary<string, string?>? extra = null)
    {
        var builder = Host.CreateApplicationBuilder();
        var settings = new Dictionary<string, string?>
        {
            ["Fleans:Streaming:Provider"] = provider,
            ["Fleans:Streaming:Kafka:Brokers"] = "localhost:9092",
            ["Fleans:Streaming:AzureQueue:ConnectionString"] = "UseDevelopmentStorage=true",
        };
        foreach (var (key, value) in extra ?? [])
            settings[key] = value;
        builder.Configuration.AddInMemoryCollection(settings);
        builder.UseOrleans(silo => silo.UseLocalhostClustering().AddFleanStreaming(builder.Configuration));
        return builder.Services.BuildServiceProvider();
    }

    private static StreamPullingAgentOptions PullingAgent(ServiceProvider services) =>
        services.GetRequiredService<IOptionsMonitor<StreamPullingAgentOptions>>()
            .Get(FleanStreamingExtensions.StreamProviderName);

    [TestMethod]
    [DataRow("Memory")]
    [DataRow("Redis")]
    [DataRow("Kafka")]
    [DataRow("AzureQueue")]
    public void Every_provider_gets_the_six_minute_delivery_window(string provider)
    {
        using var services = Build(provider);

        Assert.AreEqual(TimeSpan.FromMinutes(6), PullingAgent(services).MaxEventDeliveryTime);
    }

    [TestMethod]
    public void Configured_window_overrides_the_default()
    {
        using var services = Build("Redis", new() { ["Fleans:Streaming:MaxEventDeliveryTime"] = "00:10:00" });

        Assert.AreEqual(TimeSpan.FromMinutes(10), PullingAgent(services).MaxEventDeliveryTime);
    }

    [TestMethod]
    public void Kafka_DeliveryRetryWindow_still_wins_over_the_engine_window()
    {
        using var services = Build("Kafka", new() { ["Fleans:Streaming:Kafka:DeliveryRetryWindow"] = "00:00:30" });

        Assert.AreEqual(TimeSpan.FromSeconds(30), PullingAgent(services).MaxEventDeliveryTime);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-00:01:00")]
    [DataRow("long")]
    public void Invalid_window_is_rejected(string value)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fleans:Streaming:MaxEventDeliveryTime"] = value })
            .Build();

        var ex = Assert.ThrowsExactly<ArgumentException>(() => FleanStreamingExtensions.ReadMaxEventDeliveryTime(cfg));
        StringAssert.Contains(ex.Message, "Fleans:Streaming:MaxEventDeliveryTime must be a positive TimeSpan");
    }

    [TestMethod]
    public void Redis_keeps_100k_entries_per_stream_by_default()
    {
        using var services = Build("Redis");

        var options = services.GetRequiredService<IOptionsMonitor<RedisStreamReceiverOptions>>()
            .Get(FleanStreamingExtensions.StreamProviderName);

        Assert.AreEqual(100_000, options.MaxStreamLength);
    }
}
