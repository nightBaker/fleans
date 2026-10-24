using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streams;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// Every stream provider <see cref="FleanStreamingExtensions.AddFleanStreaming"/> registers is
/// implicit-only, so the cluster needs no rendezvous grain and no <c>PubSubStore</c> (#911).
/// </summary>
[TestClass]
public class StreamPubSubTypeTests
{
    [TestMethod]
    [DataRow("Memory")]
    [DataRow("Redis")]
    [DataRow("Kafka")]
    [DataRow("AzureQueue")]
    public void Provider_is_implicit_only(string provider)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fleans:Streaming:Provider"] = provider,
            ["Fleans:Streaming:Kafka:Brokers"] = "localhost:9092",
            ["Fleans:Streaming:AzureQueue:ConnectionString"] = "UseDevelopmentStorage=true",
        });
        builder.UseOrleans(silo => silo.UseLocalhostClustering().AddFleanStreaming(builder.Configuration));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<StreamPubSubOptions>>()
            .Get(FleanStreamingExtensions.StreamProviderName);

        Assert.AreEqual(StreamPubSubType.ImplicitOnly, options.PubSubType);
    }
}
