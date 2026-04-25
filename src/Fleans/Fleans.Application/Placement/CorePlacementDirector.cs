using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Fleans.Application.Placement;

public sealed partial class CorePlacementDirector : IPlacementDirector
{
    internal const string CorePrefix = "core-";
    internal const string CombinedPrefix = "combined-";

    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<CorePlacementDirector> _logger;
    private int _roundRobinCounter;

    public CorePlacementDirector(IGrainFactory grainFactory, ILogger<CorePlacementDirector> logger)
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
            .Where(h => HasCoreRole(h.SiloName))
            .Select(h => h.SiloAddress)
            .ToArray();

        if (candidates.Length == 0)
        {
            LogNoCoreSilo(target.GrainIdentity.Type.ToString());
            candidates = compatibleSilos;
        }

        var index = Interlocked.Increment(ref _roundRobinCounter);
        var pick = (int)((uint)index % (uint)candidates.Length);
        return candidates[pick];
    }

    internal static bool HasCoreRole(string? siloName)
    {
        if (string.IsNullOrEmpty(siloName)) return false;
        return siloName.StartsWith(CorePrefix, StringComparison.Ordinal)
            || siloName.StartsWith(CombinedPrefix, StringComparison.Ordinal);
    }

    [LoggerMessage(EventId = 11001, Level = LogLevel.Warning,
        Message = "CorePlacementDirector: no silo with 'core-' or 'combined-' prefix available for grain {grainType}; falling back to any compatible silo.")]
    private partial void LogNoCoreSilo(string grainType);
}
