namespace Fleans.Application.QueryModels;

public sealed record VariableStateSnapshot(
    Guid VariablesId,
    Dictionary<string, string> Variables);
