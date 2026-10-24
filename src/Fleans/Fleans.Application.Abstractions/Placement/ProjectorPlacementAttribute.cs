using Orleans.Placement;

namespace Fleans.Application.Placement;

/// <summary>
/// Places a grain on the read-model projector silos (<c>projector-*</c>, #861) when the cluster
/// has any, and on the engine core silos (<c>core-*</c> / <c>combined-*</c>) otherwise. Carried
/// by <c>WorkflowInstanceProjector</c> only.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ProjectorPlacementAttribute : PlacementAttribute
{
    public ProjectorPlacementAttribute() : base(ProjectorPlacementStrategy.Singleton) { }
}
