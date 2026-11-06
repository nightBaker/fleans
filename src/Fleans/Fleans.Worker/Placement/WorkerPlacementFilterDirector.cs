using Fleans.Application.Placement;
using Microsoft.Extensions.Logging;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Fleans.Worker.Placement;

/// <summary>
/// Filters the candidate silos of a <see cref="WorkerPlacementAttribute"/> grain down to
/// engine workers (<c>worker-*</c> / <c>combined-*</c>). Orleans runs it inside
/// <c>GetCompatibleSilos</c>, before the grain's placement director picks a silo — so
/// <c>[StatelessWorker]</c> still places locally when the caller is a worker silo and
/// otherwise picks one of the filtered silos.
/// </summary>
public sealed partial class WorkerPlacementFilterDirector : IPlacementFilterDirector
{
    internal const string WorkerPrefix = "worker-";
    internal const string CombinedPrefix = "combined-";

    private readonly IClusterMembershipService _membership;
    private readonly ILogger<WorkerPlacementFilterDirector> _logger;

    public WorkerPlacementFilterDirector(
        IClusterMembershipService membership,
        ILogger<WorkerPlacementFilterDirector> logger)
    {
        _membership = membership;
        _logger = logger;
    }

    public IEnumerable<SiloAddress> Filter(
        PlacementFilterStrategy filterStrategy,
        PlacementTarget target,
        IEnumerable<SiloAddress> silos)
    {
        var compatible = silos as IReadOnlyCollection<SiloAddress> ?? silos.ToArray();
        var members = _membership.CurrentSnapshot.Members;

        var workers = compatible
            .Where(s => members.TryGetValue(s, out var member) && HasWorkerRole(member.Name))
            .ToArray();

        if (workers.Length > 0)
            return workers;

        LogNoWorkerSilo(target.GrainIdentity.Type.ToString());
        return compatible;
    }

    /// <summary>
    /// Returns true when the silo name claims an engine worker role: "worker-*" / "combined-*"
    /// (silos hosting Script/Condition), as opposed to "core-*".
    /// </summary>
    internal static bool HasWorkerRole(string? siloName)
    {
        if (string.IsNullOrEmpty(siloName)) return false;
        return siloName.StartsWith(WorkerPrefix, StringComparison.Ordinal)
            || siloName.StartsWith(CombinedPrefix, StringComparison.Ordinal);
    }

    [LoggerMessage(EventId = 11301, Level = LogLevel.Warning,
        Message = "WorkerPlacementFilterDirector: no silo with 'worker-' or 'combined-' prefix available for grain {grainType}; falling back to any compatible silo.")]
    private partial void LogNoWorkerSilo(string grainType);
}
