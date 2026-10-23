using Orleans.Placement;

namespace Fleans.Worker.Placement;

/// <summary>
/// Restricts a grain to engine worker silos (<c>worker-*</c> / <c>combined-*</c>).
/// It is a placement <em>filter</em>, so it combines with the grain's placement strategy
/// (e.g. <c>[StatelessWorker]</c>) instead of replacing it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class WorkerPlacementAttribute : PlacementFilterAttribute
{
    public WorkerPlacementAttribute() : base(new WorkerPlacementFilterStrategy()) { }
}
