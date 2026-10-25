using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace Fleans.Persistence.Events;

/// <summary>
/// The WorkflowInstance journal (WorkflowEventBatches) and its snapshots (WorkflowSnapshots), both in
/// Orleans binary form — the serializer that already carries the same events and state across grain
/// calls. A journal row holds one append (see <see cref="WorkflowEventBatchEntity"/>). Used by the
/// grain's ICustomStorageInterface implementation. The read model is a separate projection
/// (<see cref="IWorkflowStateProjection"/>).
/// <para>
/// It works through <see cref="FleanJournalDbContext"/>, which can point at its own database
/// (#879). Every command it sends filters or keys by <c>InstanceId</c>, so on a store sharded by
/// it (Citus) each one is a single-shard command — keep it that way.
/// </para>
/// </summary>
public class EfCoreEventStore : IEventStore
{
    private readonly IDbContextFactory<FleanJournalDbContext> _dbContextFactory;
    private readonly Serializer _serializer;
    private readonly int _maxEventsPerLoad;

    public EfCoreEventStore(
        IDbContextFactory<FleanJournalDbContext> dbContextFactory,
        Serializer serializer,
        IOptions<FleansPersistenceOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _serializer = serializer;
        _maxEventsPerLoad = options.Value.MaxEventsPerLoad;
        SnapshotInterval = options.Value.SnapshotInterval;
    }

    public int SnapshotInterval { get; }

    /// <summary>
    /// The latest snapshot and the version it covers, or <c>(null, 0)</c> when there is none.
    /// Throws when the payload cannot be deserialized (e.g. the state shape changed).
    /// </summary>
    public async Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(Guid instanceId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var snapshot = await db.WorkflowSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.InstanceId == instanceId);
        if (snapshot is null)
            return (null, 0);

        return (_serializer.Deserialize<WorkflowInstanceState>(snapshot.Payload), snapshot.Version);
    }

    /// <summary>
    /// Loads all events after <paramref name="afterVersion"/> (an append boundary), in order.
    /// Throws when the batches are not contiguous from there, or hold more than
    /// <see cref="FleansPersistenceOptions.MaxEventsPerLoad"/> events.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowEvent>> ReadEventsAsync(Guid instanceId, int afterVersion)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        // Every batch holds at least one event, so one row past the cap is enough to detect it.
        var limit = _maxEventsPerLoad < int.MaxValue ? _maxEventsPerLoad + 1 : int.MaxValue;
        var batches = await db.WorkflowEventBatches
            .AsNoTracking()
            .Where(b => b.InstanceId == instanceId && b.FirstVersion > afterVersion)
            .OrderBy(b => b.FirstVersion)
            .Take(limit)
            .ToListAsync();

        var events = new List<WorkflowEvent>();
        var nextVersion = afterVersion + 1;
        foreach (var batch in batches)
        {
            if (batch.FirstVersion != nextVersion)
                throw new InvalidOperationException(
                    $"Journal of workflow instance '{instanceId}' has a gap: expected version {nextVersion}, " +
                    $"found a batch starting at {batch.FirstVersion} (afterVersion={afterVersion}).");

            var batchEvents = _serializer.Deserialize<List<WorkflowEvent>>(batch.Payload);
            if (batchEvents.Count != batch.LastVersion - batch.FirstVersion + 1)
                throw new InvalidOperationException(
                    $"Journal batch {batch.FirstVersion}..{batch.LastVersion} of workflow instance '{instanceId}' " +
                    $"holds {batchEvents.Count} events.");

            events.AddRange(batchEvents);
            nextVersion = batch.LastVersion + 1;

            if (events.Count > _maxEventsPerLoad)
                throw new InvalidOperationException(
                    $"Refusing to load more than {_maxEventsPerLoad} events for workflow instance '{instanceId}' " +
                    $"(afterVersion={afterVersion}). This indicates snapshotting is broken — " +
                    $"investigate WorkflowInstance.ApplyUpdatesToStorage / WriteSnapshotAsync. " +
                    $"Increase Persistence:MaxEventsPerLoad if this is intentional.");
        }

        return events;
    }

    /// <summary>
    /// Appends events as versions <c>expectedVersion + 1</c>, … (1-based; the store does the
    /// numbering so callers pass the grain's event count) in a single row tagged
    /// <paramref name="batchId"/>. A primary-key violation on <c>(InstanceId, FirstVersion)</c> means
    /// a row is already stored there: with the same batch id it is this batch, stored by an earlier
    /// try whose success was lost (#877) — true, nothing appended; otherwise another writer got
    /// there first — false. The row is read back only on that conflict, never on the common path.
    /// </summary>
    public async Task<bool> AppendEventsAsync(
        Guid instanceId,
        IReadOnlyList<WorkflowEvent> events,
        int expectedVersion,
        Guid batchId)
    {
        if (events.Count == 0) return true;

        var firstVersion = expectedVersion + 1;
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.WorkflowEventBatches.Add(new WorkflowEventBatchEntity
        {
            InstanceId = instanceId,
            FirstVersion = firstVersion,
            LastVersion = expectedVersion + events.Count,
            BatchId = batchId,
            Payload = _serializer.SerializeToArray(events.ToList())
        });

        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            var stored = await db.WorkflowEventBatches
                .AsNoTracking()
                .Where(b => b.InstanceId == instanceId && b.FirstVersion == firstVersion)
                .Select(b => new { b.BatchId, b.LastVersion })
                .FirstOrDefaultAsync();
            if (stored is null || stored.BatchId != batchId)
                return false;

            if (stored.LastVersion != expectedVersion + events.Count)
                throw new InvalidOperationException(
                    $"Journal batch {batchId} of workflow instance '{instanceId}' is stored as versions " +
                    $"{firstVersion}..{stored.LastVersion}, but its retry holds {events.Count} events.");
            return true;
        }
    }

    /// <summary>
    /// Upserts the grain's single snapshot row — an UPDATE first, so the previous payload is never
    /// read back; an INSERT only for the grain's first snapshot.
    /// </summary>
    public async Task WriteSnapshotAsync(Guid instanceId, int version, WorkflowInstanceState state)
    {
        var payload = _serializer.SerializeToArray(state);
        var now = DateTimeOffset.UtcNow;

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var updated = await db.WorkflowSnapshots
            .Where(s => s.InstanceId == instanceId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Version, version)
                .SetProperty(x => x.Payload, payload)
                .SetProperty(x => x.Timestamp, now));
        if (updated > 0)
            return;

        db.WorkflowSnapshots.Add(new WorkflowSnapshotEntity
        {
            InstanceId = instanceId,
            Version = version,
            Payload = payload,
            Timestamp = now
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes the instance's journal batches and snapshot by <c>InstanceId</c> — the journal's only
    /// access path — in one transaction.
    /// </summary>
    public async Task DeleteInstanceAsync(Guid instanceId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.WorkflowEventBatches.Where(b => b.InstanceId == instanceId).ExecuteDeleteAsync();
        await db.WorkflowSnapshots.Where(s => s.InstanceId == instanceId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    internal static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        // SQLite unique constraint violation
        if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            return true;
        // SQL Server unique constraint violation
        if (message.Contains("Cannot insert duplicate key", StringComparison.OrdinalIgnoreCase))
            return true;
        // PostgreSQL unique constraint violation
        if (message.Contains("duplicate key value violates unique constraint", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
