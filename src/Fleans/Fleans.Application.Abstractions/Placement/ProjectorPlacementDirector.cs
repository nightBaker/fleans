using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Fleans.Application.Placement;

/// <summary>
/// Director of <see cref="ProjectorPlacementAttribute"/> grains: a <c>projector-*</c> silo when
/// one is active and compatible, else a <c>core-*</c> / <c>combined-*</c> silo (dev and
/// single-deployment clusters), else any compatible silo. Round-robin within the chosen set.
/// </summary>
/// <remarks>
/// Every silo that may place the projector registers it, which is every silo that pulls stream
/// queues (core, worker, projector and plugin hosts): the pulling agent that delivers an
/// <c>InstanceAdvanced</c> event is the caller that activates the projector.
/// </remarks>
public sealed partial class ProjectorPlacementDirector : IPlacementDirector
{
    internal const string ProjectorPrefix = "projector-";

    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<ProjectorPlacementDirector> _logger;
    private int _roundRobinCounter;

    public ProjectorPlacementDirector(IGrainFactory grainFactory, ILogger<ProjectorPlacementDirector> logger)
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

        var management = _grainFactory.GetGrain<IManagementGrain>(0);
        var hosts = await management.GetDetailedHosts(onlyActive: true);

        var candidates = SelectCandidates(compatibleSilos, hosts.Select(h => (h.SiloAddress, (string?)h.SiloName)));
        if (candidates.Length == 0)
        {
            LogNoProjectorOrCoreSilo(target.GrainIdentity.Type.ToString());
            candidates = compatibleSilos;
        }

        var index = Interlocked.Increment(ref _roundRobinCounter);
        var pick = (int)((uint)index % (uint)candidates.Length);
        return candidates[pick];
    }

    /// <summary>
    /// The compatible silos the projector may go to: the <c>projector-*</c> ones when there are
    /// any, otherwise the <c>core-*</c> / <c>combined-*</c> ones; empty when there are neither.
    /// </summary>
    public static SiloAddress[] SelectCandidates(
        IReadOnlyCollection<SiloAddress> compatibleSilos,
        IEnumerable<(SiloAddress Address, string? Name)> activeHosts)
    {
        var compatible = compatibleSilos.ToHashSet();
        var hosts = activeHosts.Where(h => compatible.Contains(h.Address)).ToArray();

        var projectors = hosts.Where(h => HasProjectorRole(h.Name)).Select(h => h.Address).ToArray();
        if (projectors.Length > 0)
            return projectors;

        return hosts.Where(h => CorePlacementDirector.HasCoreRole(h.Name)).Select(h => h.Address).ToArray();
    }

    public static bool HasProjectorRole(string? siloName) =>
        !string.IsNullOrEmpty(siloName) && siloName.StartsWith(ProjectorPrefix, StringComparison.Ordinal);

    [LoggerMessage(EventId = 11302, Level = LogLevel.Warning,
        Message = "ProjectorPlacementDirector: no silo with 'projector-', 'core-' or 'combined-' prefix available for grain {grainType}; falling back to any compatible silo.")]
    private partial void LogNoProjectorOrCoreSilo(string grainType);
}
