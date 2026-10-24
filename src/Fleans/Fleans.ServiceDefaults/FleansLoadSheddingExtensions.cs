using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Fleans.ServiceDefaults;

/// <summary>
/// Orleans load shedding on a silo, off by default. When the silo's CPU passes the threshold its
/// gateway rejects new client requests (<c>GatewayTooBusyException</c>, which Fleans.Api answers with
/// 503) instead of queueing them. See <c>website/src/content/docs/reference/configuration.md</c>.
/// </summary>
public static class FleansLoadSheddingExtensions
{
    /// <summary><c>true</c> turns load shedding on. Default <c>false</c>.</summary>
    public const string EnabledKey = "Fleans:LoadShedding:Enabled";

    /// <summary>Silo CPU percentage (1–100) at which shedding starts. Default: Orleans' 95.</summary>
    public const string CpuThresholdKey = "Fleans:LoadShedding:CpuThreshold";

    /// <summary>
    /// Binds <see cref="EnabledKey"/> and <see cref="CpuThresholdKey"/> onto Orleans'
    /// <see cref="LoadSheddingOptions"/>. Orleans validates the threshold at silo startup.
    /// </summary>
    public static ISiloBuilder AddFleansLoadShedding(this ISiloBuilder siloBuilder, IConfiguration configuration)
    {
        siloBuilder.Configure<LoadSheddingOptions>(options =>
        {
            options.LoadSheddingEnabled = configuration.GetValue<bool>(EnabledKey);
            if (configuration.GetValue<int?>(CpuThresholdKey) is { } cpuThreshold)
                options.CpuThreshold = cpuThreshold;
        });
        return siloBuilder;
    }
}
