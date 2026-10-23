using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Placement;

namespace Fleans.Worker.Placement;

public static class WorkerPlacementSiloBuilderExtensions
{
    /// <summary>
    /// Registers the <see cref="WorkerPlacementAttribute"/> filter. Every silo that may place a
    /// <c>[WorkerPlacement]</c> grain needs it — Orleans throws <c>KeyNotFoundException</c>
    /// when it resolves an unregistered placement filter.
    /// </summary>
    public static ISiloBuilder AddWorkerPlacementFilter(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddPlacementFilter<WorkerPlacementFilterStrategy, WorkerPlacementFilterDirector>(
            ServiceLifetime.Singleton);
        return siloBuilder;
    }
}
