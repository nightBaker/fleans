using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>Covers the <c>Fleans:LoadShedding</c> keys bound onto Orleans' <see cref="LoadSheddingOptions"/>.</summary>
[TestClass]
public class FleansLoadSheddingExtensionsTests
{
    [TestMethod]
    public void Load_shedding_is_off_by_default_with_the_Orleans_threshold()
    {
        var options = BuildOptions(new());

        Assert.IsFalse(options.LoadSheddingEnabled);
        Assert.AreEqual(new LoadSheddingOptions().CpuThreshold, options.CpuThreshold);
    }

    [TestMethod]
    public void Config_turns_load_shedding_on_at_the_given_CPU_threshold()
    {
        var options = BuildOptions(new()
        {
            ["Fleans:LoadShedding:Enabled"] = "true",
            ["Fleans:LoadShedding:CpuThreshold"] = "80",
        });

        Assert.IsTrue(options.LoadSheddingEnabled);
        Assert.AreEqual(80, options.CpuThreshold);
    }

    private static LoadSheddingOptions BuildOptions(Dictionary<string, string?> values)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(values);
        builder.UseOrleans(silo => silo
            .UseLocalhostClustering()
            .AddFleansLoadShedding(builder.Configuration));

        // Resolving options does not start the silo.
        using var host = builder.Build();
        return host.Services.GetRequiredService<IOptions<LoadSheddingOptions>>().Value;
    }
}
