using Fleans.Domain.Events;
using Fleans.Persistence.Events;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

/// <summary>
/// Projects workflow instance state to the WorkflowInstances read model tables. Called by the
/// read-model projector (<c>WorkflowInstanceProjector</c>) with state it rebuilt from the journal
/// (#861). Each row stores the journal version it reflects (<see cref="FleanModelConfiguration.ProjectedVersion"/>),
/// and a write only lands past it: a repeated or late write never rolls the row back.
/// </summary>
/// <remarks>
/// A running instance writes only its row, in one statement (an insert, the guarded update when
/// the row is already there) and without reading it first; its
/// details are read from the grain. A completed one also writes the details the read side serves —
/// entries, variable scopes and condition results — in the same <c>SaveChanges</c> as
/// <c>IsCompleted</c>, so a reader that sees the flag sees them. Gateway forks and complex-join
/// states are cleared on completion and timer cycles are never read, so those stay with the grain.
/// </remarks>
public class EfCoreWorkflowStateProjection : IWorkflowStateProjection
{
    private const string ProjectedVersion = FleanModelConfiguration.ProjectedVersion;

    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreWorkflowStateProjection(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<WorkflowInstanceRow?> ReadRowAsync(string grainId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId);

        return await db.WorkflowInstances
            .Where(s => s.Id == id)
            .Select(s => new WorkflowInstanceRow(
                s.ProcessDefinitionId,
                s.BusinessId,
                s.CreatedAt,
                s.ParentWorkflowInstanceId,
                s.ParentActivityId,
                s.IsStarted,
                s.ExecutionStartedAt,
                s.IsCompleted,
                s.IsCancelled,
                s.IsFailed,
                s.CompletedAt))
            .SingleOrDefaultAsync();
    }

    public async Task WriteAsync(string grainId, WorkflowInstanceState state, int version)
    {
        var row = WorkflowInstanceRow.From(state);
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId);

        if (!state.IsCompleted)
        {
            await UpsertRowAsync(db, id, row, version);
            return;
        }

        try
        {
            await WriteCompletedAsync(db, id, row, state, version, loadDetails: false);
        }
        catch (DbUpdateException ex) when (EfCoreEventStore.IsUniqueConstraintViolation(ex))
        {
            // Details stored under a row that does not say completed: not something this
            // projection writes, but diff against them rather than fail on every redelivery.
            await using var retry = await _dbContextFactory.CreateDbContextAsync();
            await WriteCompletedAsync(retry, id, row, state, version, loadDetails: true);
        }
    }

    private static async Task WriteCompletedAsync(
        FleanCommandDbContext db, Guid id, WorkflowInstanceRow row, WorkflowInstanceState state, int version, bool loadDetails)
    {
        // The row is its own entity, never the projected state: tracking that would make EF add
        // every collection too. The details are added below as the state's own objects; the
        // context is gone once the write returns.
        var existing = await db.WorkflowInstances.FirstOrDefaultAsync(e => e.Id == id);
        if (existing is null)
        {
            existing = AddRow(db, id, row, version);
        }
        else
        {
            var stored = db.Entry(existing);
            if (stored.Property<int>(ProjectedVersion).CurrentValue >= version)
                return;
            // Details are written only together with IsCompleted, so a row that was still running
            // has none to diff against: skip the three loads (#903).
            var hasDetails = loadDetails || existing.IsCompleted;
            stored.CurrentValues.SetValues(row);
            stored.Property<int>(ProjectedVersion).CurrentValue = version;
            if (hasDetails)
            {
                await stored.Collection(e => e.Entries).LoadAsync();
                await stored.Collection(e => e.VariableStates).LoadAsync();
                await stored.Collection(e => e.ConditionSequenceStates).LoadAsync();
            }
        }

        DiffEntries(db, existing, state, id);
        DiffVariableStates(db, existing, state, id);
        DiffConditionSequenceStates(db, existing, state, id);

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts the row, or — when it is already there — one <c>UPDATE</c> by key guarded by the
    /// stored version. Insert first: a running instance's row changes on very few events, so a
    /// running write is almost always the instance's first, and then it costs one statement
    /// instead of an update that finds nothing, an existence check and the insert (#903). A
    /// primary-key conflict means the row exists (a reactivated or duplicate projector); the
    /// guarded update then lands only past its version. Value converters apply to
    /// <c>ExecuteUpdate</c>, so SQLite's DateTimeOffset-as-string mapping holds.
    /// </summary>
    private static async Task UpsertRowAsync(FleanCommandDbContext db, Guid id, WorkflowInstanceRow row, int version)
    {
        AddRow(db, id, row, version);
        try
        {
            await db.SaveChangesAsync();
            return;
        }
        catch (DbUpdateException ex) when (EfCoreEventStore.IsUniqueConstraintViolation(ex))
        {
            db.ChangeTracker.Clear();
        }

        await db.WorkflowInstances
            .Where(e => e.Id == id && EF.Property<int>(e, ProjectedVersion) < version)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.ProcessDefinitionId, row.ProcessDefinitionId)
                .SetProperty(e => e.BusinessId, row.BusinessId)
                .SetProperty(e => e.CreatedAt, row.CreatedAt)
                .SetProperty(e => e.ParentWorkflowInstanceId, row.ParentWorkflowInstanceId)
                .SetProperty(e => e.ParentActivityId, row.ParentActivityId)
                .SetProperty(e => e.IsStarted, row.IsStarted)
                .SetProperty(e => e.ExecutionStartedAt, row.ExecutionStartedAt)
                .SetProperty(e => e.IsCompleted, row.IsCompleted)
                .SetProperty(e => e.IsCancelled, row.IsCancelled)
                .SetProperty(e => e.IsFailed, row.IsFailed)
                .SetProperty(e => e.CompletedAt, row.CompletedAt)
                .SetProperty(e => EF.Property<int>(e, ProjectedVersion), version));
    }

    private static WorkflowInstanceState AddRow(FleanCommandDbContext db, Guid id, WorkflowInstanceRow row, int version)
    {
        var entity = new WorkflowInstanceState();
        db.WorkflowInstances.Add(entity);
        var entry = db.Entry(entity);
        entry.CurrentValues.SetValues(row);
        entry.Property(s => s.Id).CurrentValue = id;
        entry.Property<int>(ProjectedVersion).CurrentValue = version;
        return entity;
    }

    private static void DiffEntries(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        var existingDict = existing.GetEntriesByIdCache();
        var incomingDict = incoming.GetEntriesByIdCache();

        // Remove
        foreach (var old in existing.Entries.Where(e => !incomingDict.ContainsKey(e.ActivityInstanceId)))
            db.Remove(old);

        // Add or update
        foreach (var entry in incoming.Entries)
        {
            if (existingDict.TryGetValue(entry.ActivityInstanceId, out var tracked))
            {
                db.Entry(tracked).CurrentValues.SetValues(entry);
            }
            else
            {
                db.Entry(entry).Property(e => e.WorkflowInstanceId).CurrentValue = instanceId;
                db.Add(entry);
            }
        }
    }

    private static void DiffVariableStates(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        var existingDict = existing.VariableStates.ToDictionary(v => v.Id);
        var incomingDict = incoming.VariableStates.ToDictionary(v => v.Id);

        foreach (var old in existing.VariableStates.Where(v => !incomingDict.ContainsKey(v.Id)))
            db.Remove(old);

        foreach (var vs in incoming.VariableStates)
        {
            if (existingDict.TryGetValue(vs.Id, out var tracked))
            {
                db.Entry(tracked).CurrentValues.SetValues(vs);
            }
            else
            {
                db.Entry(vs).Property(v => v.WorkflowInstanceId).CurrentValue = instanceId;
                db.Add(vs);
            }
        }
    }

    /// <summary>Keyed by the table's primary key, like the other diffs — linear, not O(C²).</summary>
    private static void DiffConditionSequenceStates(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        static (Guid, string) Key(ConditionSequenceState c) => (c.GatewayActivityInstanceId, c.ConditionalSequenceFlowId);

        var existingDict = existing.ConditionSequenceStates.ToDictionary(Key);
        var incomingKeys = incoming.ConditionSequenceStates.Select(Key).ToHashSet();

        foreach (var old in existing.ConditionSequenceStates.Where(c => !incomingKeys.Contains(Key(c))))
            db.Remove(old);

        foreach (var cs in incoming.ConditionSequenceStates)
        {
            if (existingDict.TryGetValue(Key(cs), out var tracked))
            {
                db.Entry(tracked).CurrentValues.SetValues(cs);
            }
            else
            {
                db.Entry(cs).Property(c => c.WorkflowInstanceId).CurrentValue = instanceId;
                db.Add(cs);
            }
        }
    }
}
