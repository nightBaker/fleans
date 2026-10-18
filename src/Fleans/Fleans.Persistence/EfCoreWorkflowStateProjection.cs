using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

/// <summary>
/// Projects the current workflow instance state to the WorkflowInstances read model tables.
/// Called by the WorkflowInstance grain after its events are appended. Uses upsert semantics
/// without ETag checking: the event store is the source of truth, and this projection is never
/// read back as grain state. The version it reflects is a column of the same row
/// (<see cref="FleanModelConfiguration.ProjectedVersion"/>) so a grain can tell, on activation,
/// that a projection write was missed and rebuild it.
/// </summary>
/// <remarks>
/// A running instance writes only its row; its details are read from the grain. A completed one
/// also writes the details the read side serves — entries, variable scopes and condition results —
/// in the same <c>SaveChanges</c> as <c>IsCompleted</c>, so a reader that sees the flag sees them.
/// Gateway forks and complex-join states are cleared on completion and timer cycles are never
/// read, so those stay with the grain.
/// </remarks>
public class EfCoreWorkflowStateProjection : IWorkflowStateProjection
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreWorkflowStateProjection(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<int> ReadVersionAsync(string grainId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId);

        return await db.WorkflowInstances
            .Where(s => s.Id == id)
            .Select(s => (int?)EF.Property<int>(s, FleanModelConfiguration.ProjectedVersion))
            .SingleOrDefaultAsync() ?? 0;
    }

    public async Task WriteAsync(string grainId, WorkflowInstanceState state, int version)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId);
        var withDetails = state.IsCompleted;

        // The row is its own entity, never the grain's state: tracking that would make EF add
        // every collection of a running instance too. A completed instance's details are added
        // below as the grain's own objects; the context is gone once the write returns.
        var row = await db.WorkflowInstances.FirstOrDefaultAsync(e => e.Id == id);
        if (row is null)
        {
            row = new WorkflowInstanceState();
            db.WorkflowInstances.Add(row);
            db.Entry(row).CurrentValues.SetValues(state);
            db.Entry(row).Property(s => s.Id).CurrentValue = id;
        }
        else
        {
            db.Entry(row).CurrentValues.SetValues(state);
            db.Entry(row).Property(s => s.Id).IsModified = false;
        }
        db.Entry(row).Property(s => s.ETag).CurrentValue = Guid.NewGuid().ToString("N");
        db.Entry(row).Property(FleanModelConfiguration.ProjectedVersion).CurrentValue = version;

        if (withDetails)
        {
            var tracked = db.Entry(row);
            if (tracked.State != EntityState.Added)
            {
                await tracked.Collection(e => e.Entries).LoadAsync();
                await tracked.Collection(e => e.VariableStates).LoadAsync();
                await tracked.Collection(e => e.ConditionSequenceStates).LoadAsync();
            }

            DiffEntries(db, row, state, id);
            DiffVariableStates(db, row, state, id);
            DiffConditionSequenceStates(db, row, state, id);
        }

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
