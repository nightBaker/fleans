using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

/// <summary>
/// Projects workflow instance state to the WorkflowInstances read model tables. Called by the
/// WorkflowInstance grain after its events are appended, and only when the row changed (or the
/// instance is completed): the event store is the source of truth, and this projection is never
/// read back as grain state. On activation the grain compares the stored row with the journal's
/// state (<see cref="ReadRowAsync"/>) to repair a write that was lost.
/// </summary>
/// <remarks>
/// A running instance writes only its row, in one statement; its details are read from the grain.
/// A completed one also writes the details the read side serves — entries, variable scopes and
/// condition results — in the same <c>SaveChanges</c> as <c>IsCompleted</c>, so a reader that sees
/// the flag sees them. Gateway forks and complex-join states are cleared on completion and timer
/// cycles are never read, so those stay with the grain.
/// </remarks>
public class EfCoreWorkflowStateProjection : IWorkflowStateProjection
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreWorkflowStateProjection(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<WorkflowInstanceRow?> ReadRowAsync(Guid instanceId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.WorkflowInstances
            .Where(s => s.Id == instanceId)
            .Select(s => new WorkflowInstanceRow(
                s.IsStarted, s.IsCompleted, s.IsCancelled, s.IsFailed,
                s.CreatedAt, s.ExecutionStartedAt, s.CompletedAt,
                s.ProcessDefinitionId, s.ParentWorkflowInstanceId, s.ParentActivityId, s.BusinessId))
            .SingleOrDefaultAsync();
    }

    public async Task WriteRowAsync(Guid instanceId, WorkflowInstanceRow row)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        // An UPDATE first, so the row is never read; an INSERT only for the instance's first write.
        var updated = await db.WorkflowInstances
            .Where(s => s.Id == instanceId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.IsStarted, row.IsStarted)
                .SetProperty(e => e.IsCompleted, row.IsCompleted)
                .SetProperty(e => e.IsCancelled, row.IsCancelled)
                .SetProperty(e => e.IsFailed, row.IsFailed)
                .SetProperty(e => e.CreatedAt, row.CreatedAt)
                .SetProperty(e => e.ExecutionStartedAt, row.ExecutionStartedAt)
                .SetProperty(e => e.CompletedAt, row.CompletedAt)
                .SetProperty(e => e.ProcessDefinitionId, row.ProcessDefinitionId)
                .SetProperty(e => e.ParentWorkflowInstanceId, row.ParentWorkflowInstanceId)
                .SetProperty(e => e.ParentActivityId, row.ParentActivityId)
                .SetProperty(e => e.BusinessId, row.BusinessId));
        if (updated > 0)
            return;

        var entity = new WorkflowInstanceState();
        db.WorkflowInstances.Add(entity);
        db.Entry(entity).CurrentValues.SetValues(row);
        db.Entry(entity).Property(s => s.Id).CurrentValue = instanceId;
        await db.SaveChangesAsync();
    }

    public async Task WriteCompletedAsync(Guid instanceId, WorkflowInstanceState state)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        // The row is its own entity, never the grain's state: tracking that would make EF add
        // every collection too. The details are added below as the grain's own objects; the
        // context is gone once the write returns.
        // The columns come from the same WorkflowInstanceRow a running instance writes, so the
        // grain's activation check reads back exactly what was written.
        var row = await db.WorkflowInstances.FirstOrDefaultAsync(e => e.Id == instanceId);
        if (row is null)
        {
            row = new WorkflowInstanceState();
            db.WorkflowInstances.Add(row);
            db.Entry(row).CurrentValues.SetValues(WorkflowInstanceRow.From(state));
            db.Entry(row).Property(s => s.Id).CurrentValue = instanceId;
        }
        else
        {
            db.Entry(row).CurrentValues.SetValues(WorkflowInstanceRow.From(state));
            await db.Entry(row).Collection(e => e.Entries).LoadAsync();
            await db.Entry(row).Collection(e => e.VariableStates).LoadAsync();
            await db.Entry(row).Collection(e => e.ConditionSequenceStates).LoadAsync();
        }

        DiffEntries(db, row, state, instanceId);
        DiffVariableStates(db, row, state, instanceId);
        DiffConditionSequenceStates(db, row, state, instanceId);

        await db.SaveChangesAsync();
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

    private static void DiffConditionSequenceStates(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        var existingSet = existing.ConditionSequenceStates.ToHashSet();
        var incomingSet = incoming.ConditionSequenceStates.ToHashSet();

        foreach (var old in existing.ConditionSequenceStates)
        {
            if (!incoming.ConditionSequenceStates.Any(c =>
                c.GatewayActivityInstanceId == old.GatewayActivityInstanceId &&
                c.ConditionalSequenceFlowId == old.ConditionalSequenceFlowId))
            {
                db.Remove(old);
            }
        }

        foreach (var cs in incoming.ConditionSequenceStates)
        {
            var tracked = existing.ConditionSequenceStates.FirstOrDefault(c =>
                c.GatewayActivityInstanceId == cs.GatewayActivityInstanceId &&
                c.ConditionalSequenceFlowId == cs.ConditionalSequenceFlowId);

            if (tracked is not null)
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
