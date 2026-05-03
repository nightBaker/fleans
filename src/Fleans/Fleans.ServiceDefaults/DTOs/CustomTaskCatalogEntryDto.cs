using System.Text.Json.Serialization;
using Fleans.Application.CustomTasks;

namespace Fleans.ServiceDefaults.DTOs;

/// <summary>Wire DTO for <c>GET /custom-tasks</c>. One per registered task type.</summary>
public sealed record CustomTaskCatalogEntryDto(
    string TaskType,
    string? DisplayName,
    CustomTaskParameterSchemaDto? ParameterSchema,
    IReadOnlyList<string> SiloNames);

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
