namespace Fleans.Application.CustomTasks;

/// <summary>
/// What the catalog returns to API/UI consumers. Aggregates registrations across silos
/// for a given task type, so the UI can render "rest-call — 2 silos: worker-A, worker-B".
/// A private plugin whose hosts are all gone stays listed with no <see cref="SiloNames"/>:
/// its restriction still applies and its allow-list can still be managed.
/// <see cref="ParameterSchema"/> is the schema of the version <c>latest</c> selects, the first of
/// <see cref="Versions"/>.
/// </summary>
[GenerateSerializer]
public sealed record CustomTaskCatalogEntry(
    [property: Id(0)] string TaskType,
    [property: Id(1)] string? DisplayName,
    [property: Id(2)] CustomTaskParameterSchema? ParameterSchema,
    [property: Id(3)] IReadOnlyList<string> SiloNames,
    [property: Id(4)] string? Category = null,
    [property: Id(5)] CustomTaskVisibility Visibility = CustomTaskVisibility.Public,
    [property: Id(6)] List<string>? AllowedProcessKeys = null,
    [property: Id(7)] List<CustomTaskVersionEntry>? Versions = null)
{
    /// <summary>Whether a deployment of <paramref name="processKey"/> may reference this plugin.</summary>
    public bool IsUsableBy(string? processKey) =>
        Visibility == CustomTaskVisibility.Public
        || (processKey is not null && AllowedProcessKeys?.Contains(processKey, StringComparer.Ordinal) == true);

    /// <summary>Whether to offer the plugin to someone editing <paramref name="processKey"/>: usable, and hosted right now.</summary>
    public bool IsOfferedTo(string? processKey) => SiloNames.Count > 0 && IsUsableBy(processKey);
}
