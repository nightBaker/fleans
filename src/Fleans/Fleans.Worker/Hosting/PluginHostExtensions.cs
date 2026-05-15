using Microsoft.Extensions.Configuration;
using Orleans.Hosting;

namespace Fleans.Worker.Hosting;

public static class PluginHostExtensions
{
    public static ISiloBuilder AddFleansPluginHost(this ISiloBuilder siloBuilder, IConfiguration configuration)
    {
        var role = ValidatePluginRole(configuration["Fleans:Role"]);
        var siloName = BuildSiloName(role, Environment.MachineName, Guid.NewGuid());

        siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);
        return siloBuilder;
    }

    internal static string ValidatePluginRole(string? configured)
    {
        var role = (configured ?? "Plugin").ToLowerInvariant();

        if (role == "core" || role == "worker")
        {
            throw new InvalidOperationException(
                $"Fleans:Role='{configured}' is not valid for a custom plugin host. " +
                "Use 'Plugin' (recommended) or 'Combined'. Engine roles 'Core' and 'Worker' " +
                "are reserved for the Fleans engine silos (Fleans.Api, Fleans.WorkerHost).");
        }

        if (role != "plugin" && role != "combined")
        {
            throw new InvalidOperationException(
                $"Unknown Fleans:Role value '{configured}'. Valid for plugin hosts: Plugin, Combined.");
        }

        return role;
    }

    internal static string BuildSiloName(string role, string machine, Guid id)
        => $"{role}-{machine}-{id:N}".ToLowerInvariant();
}
