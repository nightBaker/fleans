using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence.PostgreSql;

/// <summary>
/// The journal on Citus (<c>Persistence:Journal:Provider=Citus</c>, #879): the two journal tables
/// become hash-distributed by <c>InstanceId</c>, the snapshots colocated with the event batches, so
/// an instance's batches and snapshot sit on one shard. Every journal command filters or keys by
/// <c>InstanceId</c> (see <c>EfCoreEventStore</c>), so each one routes to a single shard with plain
/// PostgreSQL semantics: the primary-key conflict that makes an append optimistic still holds.
/// Other tables stay local to the coordinator.
/// </summary>
public static class CitusJournal
{
    /// <summary>
    /// Shards of the journal tables. Fixed with headroom: a table's shard count cannot change
    /// without redistributing it, while shards move between nodes freely as the cluster grows.
    /// </summary>
    public const int ShardCount = 128;

    /// <summary>
    /// Distributes the journal tables of <paramref name="journal"/>'s database, once — tables that
    /// are already distributed are left alone. Run after the journal migrations, while the tables
    /// are still empty. Throws when the <c>citus</c> extension is not installed in that database.
    /// </summary>
    public static async Task DistributeAsync(FleanJournalDbContext journal, CancellationToken cancellationToken = default)
    {
        var connection = journal.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await journal.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citus')";
                if (!(bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false))
                    throw new InvalidOperationException(
                        "Persistence:Journal:Provider is Citus, but the 'citus' extension is not installed in " +
                        $"the journal database '{connection.Database}'. Run CREATE EXTENSION citus; in it first.");
            }

            // Mixed-case names: the table is a regclass and colocate_with a qualified name, so both
            // are quoted; the distribution column is matched as written.
            await journal.Database.ExecuteSqlRawAsync(
                $"""
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_dist_partition WHERE logicalrelid = '"WorkflowEventBatches"'::regclass) THEN
                    PERFORM create_distributed_table('"WorkflowEventBatches"', 'InstanceId', shard_count := {ShardCount});
                  END IF;
                  IF NOT EXISTS (SELECT 1 FROM pg_dist_partition WHERE logicalrelid = '"WorkflowSnapshots"'::regclass) THEN
                    PERFORM create_distributed_table('"WorkflowSnapshots"', 'InstanceId', colocate_with := '"WorkflowEventBatches"');
                  END IF;
                END $$;
                """,
                cancellationToken);
        }
        finally
        {
            if (opened) await journal.Database.CloseConnectionAsync();
        }
    }
}
