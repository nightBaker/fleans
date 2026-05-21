namespace Fleans.Persistence;

/// <summary>
/// Holds persistence settings injected into persistence-layer services.
/// Registered once during AddFleansPersistence() via Configure&lt;FleansPersistenceOptions&gt;.
/// </summary>
public sealed class FleansPersistenceOptions
{
    public string Provider { get; set; } = "Sqlite";

    /// <summary>
    /// Maximum number of events the event store will materialise in a single
    /// ReadEventsAsync call. Throws <see cref="InvalidOperationException"/> when exceeded.
    /// Default 1000 — ~10× the 100-event snapshot cadence
    /// (see WorkflowInstance.ApplyUpdatesToStorage).
    /// </summary>
    public int MaxEventsPerLoad { get; set; } = 1000;

    /// <summary>
    /// Reminder backing store. Bound from <c>Fleans:Reminders:Provider</c>; default
    /// <c>Redis</c> (matches the #650 ship). When set to <c>Postgres</c>,
    /// <see cref="Provider"/> must also be <c>Postgres</c> — mixed storage is
    /// rejected at startup (see #669).
    /// </summary>
    public string RemindersProvider { get; set; } = "Redis";
}
