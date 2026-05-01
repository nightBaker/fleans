namespace Fleans.Application.CustomTasks;

/// <summary>
/// What a Worker silo announces about a custom-task plugin it hosts. Sent to the
/// catalog at silo startup by the per-silo <c>CustomTaskPluginRegistrar</c>.
/// </summary>
[GenerateSerializer]
public sealed record CustomTaskRegistration(
    [property: Id(0)] string TaskType,
    [property: Id(1)] string? DisplayName,
    [property: Id(2)] CustomTaskParameterSchema? ParameterSchema,
    [property: Id(3)] string SiloName);
