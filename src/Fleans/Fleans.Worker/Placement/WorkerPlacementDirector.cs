using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Fleans.Worker.Placement;

public sealed partial class WorkerPlacementDirector : IPlacementDirector
{
    internal const string WorkerPrefix = "worker-";
    internal const string CombinedPrefix = "combined-";

    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkerPlacementDirector> _logger;
    private int _roundRobinCounter;

    public WorkerPlacementDirector(IGrainFactory grainFactory, ILogger<WorkerPlacementDirector> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task<SiloAddress> OnAddActivation(
        PlacementStrategy strategy,
        PlacementTarget target,
        IPlacementContext context)
    {
        var compatibleSilos = context.GetCompatibleSilos(target);
        if (compatibleSilos.Length == 0)
        {
            throw new OrleansException(
                $"No compatible silos are available to place grain '{target.GrainIdentity.Type}'.");
        }

        var compatibleSet = compatibleSilos.ToHashSet();
        var management = _grainFactory.GetGrain<IManagementGrain>(0);
        var hosts = await management.GetDetailedHosts(onlyActive: true);

        var candidates = hosts
            .Where(h => compatibleSet.Contains(h.SiloAddress))
            .Where(h => HasWorkerRole(h.SiloName))
            .Select(h => h.SiloAddress)
            .ToArray();

        if (candidates.Length == 0)
        {
            LogNoWorkerSilo(target.GrainIdentity.Type.ToString());
            candidates = compatibleSilos;
        }

        var index = Interlocked.Increment(ref _roundRobinCounter);
        var pick = (int)((uint)index % (uint)candidates.Length);
        return candidates[pick];
    }

    internal static bool HasWorkerRole(string? siloName)
    {
        if (string.IsNullOrEmpty(siloName)) return false;
        return siloName.StartsWith(WorkerPrefix, StringComparison.Ordinal)
            || siloName.StartsWith(CombinedPrefix, StringComparison.Ordinal);
    }

    [LoggerMessage(EventId = 11101, Level = LogLevel.Warning,
        Message = "WorkerPlacementDirector: no silo with 'worker-' or 'combined-' prefix available for grain {grainType}; falling back to any compatible silo.")]
    private partial void LogNoWorkerSilo(string grainType);
}
