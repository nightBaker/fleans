namespace Fleans.Application.CustomTasks;

/// <summary>
/// What the catalog returns to API/UI consumers. Aggregates registrations across silos
/// for a given task type, so the UI can render "rest-call — 2 silos: worker-A, worker-B".
/// </summary>
[GenerateSerializer]
public sealed record CustomTaskCatalogEntry(
    [property: Id(0)] string TaskType,
    [property: Id(1)] string? DisplayName,
    [property: Id(2)] string? ParameterSchemaJson,
    [property: Id(3)] IReadOnlyList<string> SiloNames);
