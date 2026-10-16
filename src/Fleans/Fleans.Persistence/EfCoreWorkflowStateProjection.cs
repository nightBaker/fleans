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
public class EfCoreWorkflowStateProjection : IWorkflowStateProjection
{
    // Every projected collection, derived from the enum so a new member is rebuilt too.
    private static readonly DirtyCollections AllCollections =
        Enum.GetValues<DirtyCollections>().Aggregate(DirtyCollections.None, (all, c) => all | c);

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

    public async Task WriteAsync(string grainId, WorkflowInstanceState state, int version, bool rebuild = false)
    {
        // Taken before the first await: a mutation that interleaves while this write is in
        // flight sets its flag again and is projected next time, instead of being cleared unwritten.
        var takenFlags = state.TakeDirtyFlags();
        try
        {
            var dirty = rebuild ? AllCollections : (DirtyCollections)takenFlags;
            await WriteCoreAsync(grainId, state, version, dirty);
        }
        catch
        {
            state.RestoreDirtyFlags(takenFlags);
            throw;
        }
    }

    private async Task WriteCoreAsync(string grainId, WorkflowInstanceState state, int version, DirtyCollections dirty)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId);

        // On the update path, only Include/Diff collections that were modified since
        // the last write. On the insert path, all collections are written unconditionally.
        var query = db.WorkflowInstances.AsQueryable();
        if (dirty.HasFlag(DirtyCollections.Entries))
            query = query.Include(e => e.Entries);
        if (dirty.HasFlag(DirtyCollections.VariableStates))
            query = query.Include(e => e.VariableStates);
        if (dirty.HasFlag(DirtyCollections.ConditionSequenceStates))
            query = query.Include(e => e.ConditionSequenceStates);
        if (dirty.HasFlag(DirtyCollections.GatewayForks))
            query = query.Include(e => e.GatewayForks);
        if (dirty.HasFlag(DirtyCollections.ComplexGatewayJoinStates))
            query = query.Include(e => e.ComplexGatewayJoinStates);
        if (dirty.HasFlag(DirtyCollections.TimerCycleTracking))
            query = query.Include(e => e.TimerCycleTracking);

        var existing = await query.FirstOrDefaultAsync(e => e.Id == id);

        if (existing is null)
        {
            // Insert path: write all collections unconditionally
            db.WorkflowInstances.Add(state);
            db.Entry(state).Property(s => s.Id).CurrentValue = id;
            db.Entry(state).Property(s => s.ETag).CurrentValue = Guid.NewGuid().ToString("N");
            db.Entry(state).Property(FleanModelConfiguration.ProjectedVersion).CurrentValue = version;

            foreach (var entry in state.Entries)
                db.Entry(entry).Property(e => e.WorkflowInstanceId).CurrentValue = id;
            foreach (var vs in state.VariableStates)
                db.Entry(vs).Property(v => v.WorkflowInstanceId).CurrentValue = id;
            foreach (var cs in state.ConditionSequenceStates)
                db.Entry(cs).Property(c => c.WorkflowInstanceId).CurrentValue = id;
            foreach (var gf in state.GatewayForks)
                db.Entry(gf).Property(g => g.WorkflowInstanceId).CurrentValue = id;
            foreach (var cg in state.ComplexGatewayJoinStates)
                db.Entry(cg).Property(c => c.WorkflowInstanceId).CurrentValue = id;
            foreach (var tc in state.TimerCycleTracking)
                db.Entry(tc).Property(t => t.WorkflowInstanceId).CurrentValue = id;
        }
        else
        {
            // Update path: only diff dirty collections
            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.Id).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = Guid.NewGuid().ToString("N");
            db.Entry(existing).Property(FleanModelConfiguration.ProjectedVersion).CurrentValue = version;

            if (dirty.HasFlag(DirtyCollections.Entries))
                DiffEntries(db, existing, state, id);
            if (dirty.HasFlag(DirtyCollections.VariableStates))
                DiffVariableStates(db, existing, state, id);
            if (dirty.HasFlag(DirtyCollections.ConditionSequenceStates))
                DiffConditionSequenceStates(db, existing, state, id);
            if (dirty.HasFlag(DirtyCollections.GatewayForks))
                DiffGatewayForks(db, existing, state, id);
            if (dirty.HasFlag(DirtyCollections.ComplexGatewayJoinStates))
                DiffComplexGatewayJoinStates(db, existing, state, id);
            if (dirty.HasFlag(DirtyCollections.TimerCycleTracking))
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
