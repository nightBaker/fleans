using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Fleans.Application.Placement;

public sealed partial class CorePlacementDirector : IPlacementDirector
{
    internal const string CorePrefix = "core-";
    internal const string CombinedPrefix = "combined-";

    private readonly CachedSiloHosts _hosts;
    private readonly ILogger<CorePlacementDirector> _logger;
    private int _roundRobinCounter;

    public CorePlacementDirector(IGrainFactory grainFactory, ILogger<CorePlacementDirector> logger)
    {
        _hosts = new CachedSiloHosts(
            () => grainFactory.GetGrain<IManagementGrain>(0).GetDetailedHosts(onlyActive: true));
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
        var candidates = CoreCandidates(await _hosts.GetAsync(), compatibleSet);
        // A cached list may predate the core silos that joined since (cluster start-up).
        if (candidates.Length == 0)
            candidates = CoreCandidates(await _hosts.GetAsync(forceRefresh: true), compatibleSet);

        if (candidates.Length == 0)
        {
            LogNoCoreSilo(target.GrainIdentity.Type.ToString());
            candidates = compatibleSilos;
        }

        var index = Interlocked.Increment(ref _roundRobinCounter);
        var pick = (int)((uint)index % (uint)candidates.Length);
        return candidates[pick];
    }

    private static SiloAddress[] CoreCandidates(MembershipEntry[] hosts, HashSet<SiloAddress> compatible) =>
        hosts
            .Where(h => compatible.Contains(h.SiloAddress))
            .Where(h => HasCoreRole(h.SiloName))
            .Select(h => h.SiloAddress)
            .ToArray();

    public static bool HasCoreRole(string? siloName)
    {
        if (string.IsNullOrEmpty(siloName)) return false;
        return siloName.StartsWith(CorePrefix, StringComparison.Ordinal)
            || siloName.StartsWith(CombinedPrefix, StringComparison.Ordinal);
    }

    [LoggerMessage(EventId = 11300, Level = LogLevel.Warning,
        Message = "CorePlacementDirector: no silo with 'core-' or 'combined-' prefix available for grain {grainType}; falling back to any compatible silo.")]
    private partial void LogNoCoreSilo(string grainType);
}
