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
/// also writes its details — entries, variable scopes, condition results and the rest — in the
/// same <c>SaveChanges</c> as <c>IsCompleted</c>, so a reader that sees the flag sees the details.
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

        var query = db.WorkflowInstances.AsQueryable();
        if (withDetails)
            query = query
                .Include(e => e.Entries)
                .Include(e => e.VariableStates)
                .Include(e => e.ConditionSequenceStates)
                .Include(e => e.GatewayForks)
                .Include(e => e.ComplexGatewayJoinStates)
                .Include(e => e.TimerCycleTracking)
                .AsSplitQuery();

        // The row is its own entity, never the grain's state: tracking that would make EF add
        // every collection of a running instance too.
        var row = await query.FirstOrDefaultAsync(e => e.Id == id);
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
            DiffEntries(db, row, state, id);
            DiffVariableStates(db, row, state, id);
            DiffConditionSequenceStates(db, row, state, id);
            DiffGatewayForks(db, row, state, id);
            DiffComplexGatewayJoinStates(db, row, state, id);
            DiffTimerCycleTracking(db, row, state, id);
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

    private static void DiffComplexGatewayJoinStates(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        var existingDict = existing.ComplexGatewayJoinStates.ToDictionary(c => c.GatewayActivityId);
        var incomingDict = incoming.ComplexGatewayJoinStates.ToDictionary(c => c.GatewayActivityId);

        foreach (var old in existing.ComplexGatewayJoinStates.Where(c => !incomingDict.ContainsKey(c.GatewayActivityId)))
            db.Remove(old);

        foreach (var cg in incoming.ComplexGatewayJoinStates)
        {
            if (existingDict.TryGetValue(cg.GatewayActivityId, out var tracked))
            {
                db.Entry(tracked).CurrentValues.SetValues(cg);
            }
            else
            {
                db.Entry(cg).Property(c => c.WorkflowInstanceId).CurrentValue = instanceId;
                db.Add(cg);
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
