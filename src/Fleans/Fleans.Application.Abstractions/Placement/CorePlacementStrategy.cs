using Orleans.Runtime;

namespace Fleans.Application.Placement;

[Serializable, GenerateSerializer, Immutable]
public sealed class CorePlacementStrategy : PlacementStrategy
{
    internal static readonly CorePlacementStrategy Singleton = new();
}
