namespace Fleans.ServiceDefaults;

/// <summary>
/// Holds the persistence provider choice registered once during AddFleansPersistence().
/// Resolved by EnsureDatabaseSchemaAsync() to avoid re-reading configuration.
/// </summary>
public sealed class FleansPersistenceOptions
{
    public string Provider { get; set; } = "Sqlite";
}
