using Orleans.Placement;

namespace Fleans.Application.Placement;

/// <summary>
/// Placement <em>filter</em> (not a placement strategy) that narrows a grain's candidate silos
/// to engine workers (<c>worker-*</c> / <c>combined-*</c>). Being a filter, it composes with any
/// placement strategy — in particular <c>[StatelessWorker]</c>, which a second
/// <c>PlacementAttribute</c> would silently replace (#857). Applied via
/// <see cref="WorkerPlacementAttribute"/>; filtering is done by
/// <see cref="WorkerPlacementFilterDirector"/>.
/// </summary>
public sealed class WorkerPlacementFilterStrategy : PlacementFilterStrategy
{
    public WorkerPlacementFilterStrategy() : base(order: 0) { }
}
