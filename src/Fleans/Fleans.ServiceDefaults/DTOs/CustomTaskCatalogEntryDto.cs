using System.Text.Json.Serialization;
using Fleans.Application.CustomTasks;

namespace Fleans.ServiceDefaults.DTOs;

/// <summary>Wire DTO for <c>GET /custom-tasks</c>. One per registered task type.</summary>
public sealed record CustomTaskCatalogEntryDto(
    string TaskType,
    string? DisplayName,
    CustomTaskParameterSchemaDto? ParameterSchema,
    IReadOnlyList<string> SiloNames,
    string? Category,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CustomTaskVisibility Visibility,
    IReadOnlyList<string> AllowedProcessKeys);

/// <summary>Body of <c>PUT /custom-tasks/{taskType}/allowed-processes</c>: the whole new allow-list.</summary>
public sealed record SetAllowedProcessesRequest(List<string> ProcessKeys);

/// <summary>Wire DTO for the per-plugin parameter list rendered by the management UI editor.</summary>
public sealed record CustomTaskParameterSchemaDto(
    IReadOnlyList<CustomTaskParameterSpecDto> Parameters);

/// <summary>
/// Wire DTO for one parameter the plugin's <c>ExecuteAsync</c> reads.
/// <see cref="Type"/> and <see cref="ItemType"/> serialize as their enum-name strings
/// (not integers) so the wire format stays human-readable and resilient to enum reordering.
/// </summary>
public sealed record CustomTaskParameterSpecDto(
    string Name,
    string? DisplayName,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CustomTaskParameterType Type,
    bool Required,
    string? Description,
    string? DefaultValue,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CustomTaskParameterType? ItemType);

/// <summary>Catalog records → wire DTOs; shared by <c>GET /custom-tasks</c> and the editor's assistant panel.</summary>
public static class CustomTaskCatalogMappers
{
    public static CustomTaskCatalogEntryDto ToDto(this CustomTaskCatalogEntry entry) =>
        new(entry.TaskType, entry.DisplayName, entry.ParameterSchema?.ToDto(), entry.SiloNames,
            entry.Category, entry.Visibility, entry.AllowedProcessKeys ?? []);

    public static CustomTaskParameterSchemaDto ToDto(this CustomTaskParameterSchema schema) =>
        new(schema.Parameters.Select(p => p.ToDto()).ToList());

    public static CustomTaskParameterSpecDto ToDto(this CustomTaskParameterSpec spec) =>
        new(spec.Name, spec.DisplayName, spec.Type, spec.Required,
            spec.Description, spec.DefaultValue, spec.ItemType);
}
