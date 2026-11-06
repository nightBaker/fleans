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

    /// <summary>
    /// One row per private task type. Kept apart from <see cref="Entries"/> because those rows go
    /// when their silo or worker does, and a restriction must not end with the host.
    /// </summary>
    [Id(2)] public List<CustomTaskPrivateAccessState> PrivateAccess { get; set; } = [];

    /// <summary>
    /// Idempotent upsert keyed by <c>(TaskType, SiloName)</c>, together with what the registration
    /// declares about access. Returns whether anything changed.
    /// </summary>
    public bool Upsert(string taskType, string siloName, string? displayName, string? parameterSchemaJson,
        string? category = null, bool isPrivate = false, IReadOnlyList<string>? declaredProcessKeys = null)
    {
        var changed = UpsertRow(taskType, siloName, displayName, parameterSchemaJson, category, isPrivate);
        if (isPrivate)
            changed |= ApplyDeclaredAllowList(taskType, declaredProcessKeys ?? []);
        return DropAccessOfPublicTasks() | changed;
    }

    /// <summary>Replaces a private task type's allow-list; <c>false</c> when the type is not private.</summary>
    public bool SetAllowedProcesses(string taskType, IReadOnlyList<string> processKeys)
    {
        var access = FindAccess(taskType);
        if (access is null) return false;
        access.AllowedProcessKeys = [.. processKeys];
        access.AllowListManaged = true;
        return true;
    }

    /// <summary>Whether <paramref name="processKey"/> may use the task type; anything not private is open to all.</summary>
    public bool IsAllowed(string taskType, string processKey) =>
        FindAccess(taskType) is not { } access || access.AllowedProcessKeys.Contains(processKey, StringComparer.Ordinal);

    public CustomTaskPrivateAccessState? FindAccess(string taskType) =>
        PrivateAccess.FirstOrDefault(a => string.Equals(a.TaskType, taskType, StringComparison.OrdinalIgnoreCase));

    private bool UpsertRow(string taskType, string siloName, string? displayName, string? parameterSchemaJson,
        string? category, bool isPrivate)
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
                Category = category,
                IsPrivate = isPrivate,
            });
            return true;
        }

        var changed = existing.DisplayName != displayName
            || existing.ParameterSchemaJson != parameterSchemaJson
            || existing.Category != category
            || existing.IsPrivate != isPrivate;
        existing.DisplayName = displayName;
        existing.ParameterSchemaJson = parameterSchemaJson;
        existing.Category = category;
        existing.IsPrivate = isPrivate;
        return changed;
    }

    /// <summary>The list hosts declare applies until the allow-list is first edited through the API or UI.</summary>
    private bool ApplyDeclaredAllowList(string taskType, IReadOnlyList<string> declaredProcessKeys)
    {
        var access = FindAccess(taskType);
        if (access is null)
        {
            PrivateAccess.Add(new CustomTaskPrivateAccessState
            {
                TaskType = taskType,
                AllowedProcessKeys = [.. declaredProcessKeys],
            });
            return true;
        }

        if (access.AllowListManaged || access.AllowedProcessKeys.SequenceEqual(declaredProcessKeys, StringComparer.Ordinal))
            return false;
        access.AllowedProcessKeys = [.. declaredProcessKeys];
        return true;
    }

    /// <summary>
    /// A task type stops being private once it has hosts and all of them declare it public. With no
    /// hosts it stays private: a plugin that is down must not lose its restriction. One private host
    /// among public ones keeps it private too, so a misconfigured host cannot open it up.
    /// </summary>
    private bool DropAccessOfPublicTasks() =>
        PrivateAccess.RemoveAll(access =>
        {
            var rows = Entries.Where(e => string.Equals(e.TaskType, access.TaskType, StringComparison.OrdinalIgnoreCase)).ToList();
            return rows.Count > 0 && rows.All(r => !r.IsPrivate);
        }) > 0;

    /// <summary>Removes any row matching the predicate; returns the count removed.</summary>
    public int RemoveWhere(Func<CustomTaskCatalogRowState, bool> predicate)
    {
        var removed = Entries.RemoveAll(e => predicate(e));
        if (removed > 0)
            DropAccessOfPublicTasks();
        return removed;
    }
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
    /// <summary>The editor's group for the plugin; <c>null</c> puts it in the default group.</summary>
    [Id(4)] public string? Category { get; set; }
    /// <summary>What this host declared. The restriction itself is <see cref="CustomTaskPrivateAccessState"/>.</summary>
    [Id(5)] public bool IsPrivate { get; set; }
}

/// <summary>
/// The access rule of one private task type. A public task type has no such row.
/// </summary>
[GenerateSerializer]
public class CustomTaskPrivateAccessState
{
    [Id(0)] public string TaskType { get; set; } = string.Empty;
    /// <summary>Process definition keys allowed to use the plugin. Exact match; empty allows none.</summary>
    [Id(1)] public List<string> AllowedProcessKeys { get; set; } = [];
    /// <summary>Edited through the API or UI, so the list hosts declare no longer applies.</summary>
    [Id(2)] public bool AllowListManaged { get; set; }
}
