namespace Fleans.Domain.States;

/// <summary>
/// Persisted state for <c>CustomTaskCatalogGrain</c>. One row per
/// <c>(TaskType, SiloName)</c> pair; the grain aggregates rows by task type
/// when serving <c>GetAll</c> / <c>Get</c> for the management UI.
///
/// Sub-issue A2 of #357 (PR #434): replaces the v1 in-memory dictionary
/// so the catalog survives Core silo restart.
/// </summary>
[GenerateSerializer]
public class CustomTaskCatalogState
{
    [Id(0)] public string? ETag { get; set; }
    [Id(1)] public List<CustomTaskCatalogRowState> Entries { get; set; } = [];

    /// <summary>Idempotent upsert keyed by <c>(TaskType, SiloName)</c>.</summary>
    public bool Upsert(string taskType, string siloName, string? displayName, string? parameterSchemaJson)
    {
        var existing = Entries.FirstOrDefault(e =>
            string.Equals(e.TaskType, taskType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.SiloName, siloName, StringComparison.Ordinal));

        if (existing is null)
        {
            Entries.Add(new CustomTaskCatalogRowState
            {
                TaskType = taskType,
                SiloName = siloName,
                DisplayName = displayName,
                ParameterSchemaJson = parameterSchemaJson,
            });
            return true;
        }

        var changed = existing.DisplayName != displayName
            || existing.ParameterSchemaJson != parameterSchemaJson;
        existing.DisplayName = displayName;
        existing.ParameterSchemaJson = parameterSchemaJson;
        return changed;
    }

    /// <summary>Removes any row matching the predicate; returns the count removed.</summary>
    public int RemoveWhere(Func<CustomTaskCatalogRowState, bool> predicate)
        => Entries.RemoveAll(e => predicate(e));
}

/// <summary>
/// Per-row state in the catalog. Named "<c>Row</c>" rather than "<c>Entry</c>"
/// to differentiate from the public <c>CustomTaskCatalogEntry</c> DTO returned
/// to UI consumers (which is aggregated by <c>TaskType</c> with a list of silos).
/// </summary>
[GenerateSerializer]
public class CustomTaskCatalogRowState
{
    [Id(0)] public string TaskType { get; set; } = string.Empty;
    [Id(1)] public string SiloName { get; set; } = string.Empty;
    [Id(2)] public string? DisplayName { get; set; }
    /// <summary>
    /// Serialized <c>CustomTaskParameterSchema</c> (System.Text.Json). Stored as
    /// JSON so the catalog can persist plugin metadata without schema-knowledge
    /// in the persistence layer; the catalog grain deserializes back to the typed
    /// schema when serving requests, and skips-and-warns on malformed JSON.
    /// </summary>
    [Id(3)] public string? ParameterSchemaJson { get; set; }
}
