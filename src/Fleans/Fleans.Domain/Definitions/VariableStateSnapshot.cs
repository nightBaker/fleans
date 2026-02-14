using Orleans;

namespace Fleans.Domain;

[GenerateSerializer]
public sealed record VariableStateSnapshot(
    [property: Id(0)] Guid VariablesId,
    [property: Id(1)] Dictionary<string, string> Variables);
