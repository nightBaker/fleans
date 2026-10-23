namespace Fleans.Persistence;

/// <summary>
/// Holds persistence settings injected into persistence-layer services.
/// Registered once during AddFleansPersistence() via Configure&lt;FleansPersistenceOptions&gt;.
/// </summary>
public sealed class FleansPersistenceOptions
{
    public string Provider { get; set; } = "Sqlite";

    /// <summary>
    /// The WorkflowInstance journal store (bound from <c>Persistence:Journal</c>, #879).
    /// </summary>
    public JournalPersistenceOptions Journal { get; set; } = new();

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

/// <summary>
/// Where the WorkflowInstance journal and snapshots live (<see cref="FleanJournalDbContext"/>).
/// Without <c>ConnectionStrings:fleans-journal</c> the journal shares the main database.
/// See <c>docs/conventions/persistence.md</c>.
/// </summary>
public sealed class JournalPersistenceOptions
{
    /// <summary>
    /// <c>Postgres</c> | <c>Citus</c> | <c>Sqlite</c> (case-insensitive). Empty means <c>Postgres</c>,
    /// except on a SQLite main database with no <c>ConnectionStrings:fleans-journal</c>: there the
    /// journal stays in the SQLite file (local dev). <c>Citus</c> is PostgreSQL with the journal
    /// tables distributed by <c>InstanceId</c> when the schema is applied. Holds the resolved value
    /// once options are built.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>Citus settings (<c>Persistence:Journal:Citus</c>), used when <see cref="Provider"/> is <c>Citus</c>.</summary>
    public CitusJournalOptions Citus { get; set; } = new();
}

/// <summary>How the journal tables are distributed on Citus.</summary>
public sealed class CitusJournalOptions
{
    /// <summary>Citus's upper bound for a table's shard count.</summary>
    public const int MaxShardCount = 64000;

    /// <summary>
    /// Shards of each journal table (<c>Persistence:Journal:Citus:ShardCount</c>, default 128, 1..64000).
    /// Applied when the tables are distributed — on a fresh journal database; changing it later
    /// does not reshard tables that are already distributed. Pick it with headroom: shards move
    /// between nodes freely, while changing their count means redistributing the table.
    /// </summary>
    public int ShardCount { get; set; } = 128;
}
