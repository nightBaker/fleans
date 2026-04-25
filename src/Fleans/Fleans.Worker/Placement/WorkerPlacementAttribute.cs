using Orleans.Placement;

namespace Fleans.Worker.Placement;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class WorkerPlacementAttribute : PlacementAttribute
{
    public WorkerPlacementAttribute() : base(WorkerPlacementStrategy.Singleton) { }
}
