using Fleans.Worker.Placement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Fleans.Worker.Hosting;

public static class PlacementAssertionExtensions
{
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
}
