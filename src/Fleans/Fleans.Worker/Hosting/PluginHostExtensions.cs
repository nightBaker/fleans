using Fleans.Application.Placement;
using Fleans.Worker.Placement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Fleans.Worker.Hosting;

public static class PluginHostExtensions
{
    public static ISiloBuilder AddFleansPluginHost(this ISiloBuilder siloBuilder, IConfiguration configuration)
    {
        var role = ValidatePluginRole(configuration["Fleans:Role"]);
        var siloName = BuildSiloName(role, Environment.MachineName, Guid.NewGuid());

        siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);
        siloBuilder.AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>();
        siloBuilder.AddPlacementDirector<WorkerPlacementStrategy, WorkerPlacementDirector>();
        siloBuilder.AddFleansPlacementAssertion(configuration);
        return siloBuilder;
    }

    /// <summary>
    /// Registers <see cref="PlacementRoleAssertion"/> as a silo lifecycle participant.
    /// Fails fast at startup if any DI-registered grain's <c>[WorkerPlacement]</c> /
    /// <c>[CorePlacement]</c> attribute is incompatible with the current silo's
    /// <c>Fleans:Role</c>. Idempotent — safe to call multiple times.
    /// </summary>
    public static ISiloBuilder AddFleansPlacementAssertion(this ISiloBuilder siloBuilder, IConfiguration configuration)
    {
        siloBuilder.Services.TryAddSingleton<ILifecycleParticipant<ISiloLifecycle>, PlacementRoleAssertion>();
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
