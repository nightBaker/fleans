namespace Fleans.ServiceDefaults.DTOs;

/// <summary>Wire DTO for <c>GET /custom-tasks</c>. One per registered task type.</summary>
public sealed record CustomTaskCatalogEntryDto(
    string TaskType,
    string? DisplayName,
    string? ParameterSchemaJson,
    IReadOnlyList<string> SiloNames);
