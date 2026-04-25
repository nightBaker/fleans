using Orleans.Placement;

namespace Fleans.Application.Placement;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CorePlacementAttribute : PlacementAttribute
{
    public CorePlacementAttribute() : base(CorePlacementStrategy.Singleton) { }
}
