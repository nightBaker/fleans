using Fleans.Application.Placement;
using Fleans.Worker.CustomTasks;
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
        var siloName = ResolveSiloName(configuration, role);

        siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);
        // Plugin hosts must not advertise as Orleans client gateways: their assembly load context
        // intentionally lacks Fleans.Application / Fleans.Domain / Fleans.Persistence, so any
        // engine grain call (e.g. IWorkflowInstanceGrain) forwarded *through* a plugin gateway
        // throws "Unable to load type" at the gateway. See #703.
        siloBuilder.Configure<Orleans.Configuration.EndpointOptions>(o => o.GatewayPort = 0);
        siloBuilder.AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>();
        siloBuilder.AddWorkerPlacementFilter();
        // Every handler in the host's assemblies gets a task-type grain type, registered or not.
        siloBuilder.Services.AddCustomTaskGrainTypes();
        siloBuilder.AddFleansPlacementAssertion(configuration);
        // The silo owns part of the reminder ring, so it must run the engine's reminder table (#788).
        siloBuilder.AddFleansPluginReminders(configuration);
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

    /// <summary>
    /// The silo name: <c>{role}-{machine}-{guid}</c>, or the pod name when the engine runs with
    /// <c>Fleans:Kubernetes:Enabled</c>. Engine silos with Orleans Kubernetes hosting declare every
    /// active silo whose name matches no pod dead, so on Kubernetes a plugin silo must be named
    /// after its pod — and the pod name must start with the role, since placement reads the role
    /// from the silo-name prefix. Same rule as the engine's <c>ResolveSiloName</c>.
    /// </summary>
    internal static string ResolveSiloName(IConfiguration configuration, string role)
    {
        if (!configuration.GetValue<bool>("Fleans:Kubernetes:Enabled"))
            return BuildSiloName(role, Environment.MachineName, Guid.NewGuid());

        var podName = configuration["POD_NAME"];
        if (string.IsNullOrWhiteSpace(podName))
            podName = Environment.MachineName;

        var prefix = role + "-";
        if (!podName.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Fleans:Kubernetes:Enabled=true makes the pod name the Orleans silo name, and Fleans " +
                $"placement reads the silo role from its prefix — pod '{podName}' must start with '{prefix}'. " +
                $"Name the Deployment '{prefix}…'.");

        return podName;
    }
}
