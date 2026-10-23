using Fleans.Persistence.Events;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

/// <summary>
/// The WorkflowInstance journal store: the event batches and the snapshots, used only by
/// <see cref="EfCoreEventStore"/>. A context of its own so the journal can live in a different
/// database from the read model and the other grain state (<c>Persistence:Journal:Provider</c>,
/// <c>ConnectionStrings:fleans-journal</c>, #879) with its own migrations. When no journal
/// connection is configured it shares the main database.
/// <para>
/// Both tables are accessed by <c>InstanceId</c> only — no joins, no queries across instances —
/// so a distributed store can shard them by it.
/// </para>
/// </summary>
public class FleanJournalDbContext : DbContext
{
    /// <summary>
    /// The EF migrations history table of this context. Its own name so the journal migrations
    /// can share a database with the main context's <c>__EFMigrationsHistory</c>.
    /// </summary>
    public const string MigrationsHistoryTable = "__EFJournalMigrationsHistory";

    public DbSet<WorkflowEventBatchEntity> WorkflowEventBatches => Set<WorkflowEventBatchEntity>();
    public DbSet<WorkflowSnapshotEntity> WorkflowSnapshots => Set<WorkflowSnapshotEntity>();

    public FleanJournalDbContext(DbContextOptions<FleanJournalDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        FleanModelConfiguration.ConfigureJournal(modelBuilder);
    }
}
