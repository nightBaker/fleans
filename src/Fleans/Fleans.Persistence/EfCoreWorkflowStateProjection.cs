using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

/// <summary>
/// Projects the current workflow instance state to the WorkflowInstances read model table.
/// Called by the WorkflowInstance grain after events are confirmed via JournaledGrain.
/// Uses upsert semantics without ETag checking (event store is the source of truth).
/// </summary>
public class EfCoreWorkflowStateProjection : IWorkflowStateProjection
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreWorkflowStateProjection(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<WorkflowInstanceState?> ReadAsync(string grainId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId);

        return await db.WorkflowInstances
            .Include(s => s.Entries)
            .Include(s => s.VariableStates)
            .Include(s => s.ConditionSequenceStates)
            .Include(s => s.GatewayForks)
            .Include(s => s.TimerCycleTracking)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task WriteAsync(string grainId, WorkflowInstanceState state)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId);

        var existing = await db.WorkflowInstances
            .Include(e => e.Entries)
            .Include(e => e.VariableStates)
            .Include(e => e.ConditionSequenceStates)
            .Include(e => e.GatewayForks)
            .Include(e => e.TimerCycleTracking)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (existing is null)
        {
            db.WorkflowInstances.Add(state);
            db.Entry(state).Property(s => s.Id).CurrentValue = id;
            db.Entry(state).Property(s => s.ETag).CurrentValue = Guid.NewGuid().ToString("N");

            foreach (var entry in state.Entries)
                db.Entry(entry).Property(e => e.WorkflowInstanceId).CurrentValue = id;
            foreach (var vs in state.VariableStates)
                db.Entry(vs).Property(v => v.WorkflowInstanceId).CurrentValue = id;
            foreach (var cs in state.ConditionSequenceStates)
                db.Entry(cs).Property(c => c.WorkflowInstanceId).CurrentValue = id;
            foreach (var gf in state.GatewayForks)
                db.Entry(gf).Property(g => g.WorkflowInstanceId).CurrentValue = id;
            foreach (var tc in state.TimerCycleTracking)
                db.Entry(tc).Property(t => t.WorkflowInstanceId).CurrentValue = id;
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.Id).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = Guid.NewGuid().ToString("N");

            DiffEntries(db, existing, state, id);
            DiffVariableStates(db, existing, state, id);
            DiffConditionSequenceStates(db, existing, state, id);
            DiffGatewayForks(db, existing, state, id);
            DiffTimerCycleTracking(db, existing, state, id);
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

    private static void DiffGatewayForks(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        var existingDict = existing.GatewayForks.ToDictionary(g => g.ForkInstanceId);
        var incomingDict = incoming.GatewayForks.ToDictionary(g => g.ForkInstanceId);

        foreach (var old in existing.GatewayForks.Where(g => !incomingDict.ContainsKey(g.ForkInstanceId)))
            db.Remove(old);

        foreach (var gf in incoming.GatewayForks)
        {
            if (existingDict.TryGetValue(gf.ForkInstanceId, out var tracked))
            {
                db.Entry(tracked).CurrentValues.SetValues(gf);
            }
            else
            {
                db.Entry(gf).Property(g => g.WorkflowInstanceId).CurrentValue = instanceId;
                db.Add(gf);
            }
        }
    }

    private static void DiffTimerCycleTracking(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        var existingByKey = existing.TimerCycleTracking
            .ToDictionary(t => (t.HostActivityInstanceId, t.TimerActivityId));
        var incomingKeys = incoming.TimerCycleTracking
            .Select(t => (t.HostActivityInstanceId, t.TimerActivityId))
            .ToHashSet();

        foreach (var old in existing.TimerCycleTracking
            .Where(t => !incomingKeys.Contains((t.HostActivityInstanceId, t.TimerActivityId))))
            db.Remove(old);

        foreach (var tc in incoming.TimerCycleTracking)
        {
            var key = (tc.HostActivityInstanceId, tc.TimerActivityId);
            if (existingByKey.TryGetValue(key, out var tracked))
            {
                db.Entry(tracked).CurrentValues.SetValues(tc);
            }
            else
            {
                db.Entry(tc).Property(t => t.WorkflowInstanceId).CurrentValue = instanceId;
                db.Add(tc);
            }
        }
    }
}
