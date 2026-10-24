using Orleans.Runtime;

namespace Fleans.Application.Placement;

[Serializable, GenerateSerializer, Immutable]
public sealed class ProjectorPlacementStrategy : PlacementStrategy
{
    internal static readonly ProjectorPlacementStrategy Singleton = new();
}
