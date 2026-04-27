using Orleans.Runtime;

namespace Fleans.Worker.Placement;

[Serializable, GenerateSerializer, Immutable]
public sealed class WorkerPlacementStrategy : PlacementStrategy
{
    internal static readonly WorkerPlacementStrategy Singleton = new();
}
