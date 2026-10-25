using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// <c>Fleans:Streaming:PollPeriod</c> sets how often every provider's pulling agents read their
/// queues (Orleans' <c>GetQueueMsgsTimerPeriod</c>); absent, Orleans' 100 ms default applies.
/// </summary>
[TestClass]
public class StreamPollPeriodTests
{
    private static StreamPullingAgentOptions Resolve(string provider, string? pollPeriod)
    {
        var builder = Host.CreateApplicationBuilder();
        var settings = new Dictionary<string, string?>
        {
            ["Fleans:Streaming:Provider"] = provider,
            ["Fleans:Streaming:Kafka:Brokers"] = "localhost:9092",
            ["Fleans:Streaming:AzureQueue:ConnectionString"] = "UseDevelopmentStorage=true",
        };
        if (pollPeriod is not null)
            settings["Fleans:Streaming:PollPeriod"] = pollPeriod;
        builder.Configuration.AddInMemoryCollection(settings);
        builder.UseOrleans(silo => silo.UseLocalhostClustering().AddFleanStreaming(builder.Configuration));

        using var services = builder.Services.BuildServiceProvider();
        return services.GetRequiredService<IOptionsMonitor<StreamPullingAgentOptions>>()
            .Get(FleanStreamingExtensions.StreamProviderName);
    }

    [TestMethod]
    [DataRow("Memory")]
    [DataRow("Redis")]
    [DataRow("Kafka")]
    [DataRow("AzureQueue")]
    public void Configured_period_reaches_the_pulling_agent(string provider)
    {
        var options = Resolve(provider, "00:00:00.020");

        Assert.AreEqual(TimeSpan.FromMilliseconds(20), options.GetQueueMsgsTimerPeriod);
    }

    [TestMethod]
    public void Absent_period_keeps_the_orleans_default()
    {
        var options = Resolve("Redis", pollPeriod: null);

        Assert.AreEqual(TimeSpan.FromMilliseconds(100), options.GetQueueMsgsTimerPeriod);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-00:00:01")]
    [DataRow("fast")]
    public void Invalid_period_is_rejected(string value)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fleans:Streaming:PollPeriod"] = value })
            .Build();

        var ex = Assert.ThrowsExactly<ArgumentException>(() => FleanStreamingExtensions.ReadPollPeriod(cfg));
        StringAssert.Contains(ex.Message, "Fleans:Streaming:PollPeriod must be a positive TimeSpan");
    }
}
