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
    /// Default 1000 — 10× the default <see cref="SnapshotInterval"/>, so it only trips when
    /// snapshots stopped being written or could not be read.
    /// </summary>
    public int MaxEventsPerLoad { get; set; } = 1000;

    /// <summary>
    /// A workflow instance snapshot is written once this many events were appended since the
    /// previous one (bound from <c>Persistence:SnapshotInterval</c>). Activation replays at most
    /// about this many events after the snapshot. Default 100.
    /// </summary>
    public int SnapshotInterval { get; set; } = 100;

    /// <summary>
    /// Reminder backing store. Bound from <c>Fleans:Reminders:Provider</c>; default
    /// <c>Redis</c> (matches the #650 ship). When set to <c>Postgres</c>,
    /// <see cref="Provider"/> must also be <c>Postgres</c> — mixed storage is
    /// rejected at startup (see #669).
    /// </summary>
    public string RemindersProvider { get; set; } = "Redis";
}
