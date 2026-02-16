using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreWorkflowInstanceGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreWorkflowInstanceGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.GetGuidKey();

        var state = await db.WorkflowInstances
            .Include(s => s.Entries)
            .Include(s => s.VariableStates)
            .Include(s => s.ConditionSequenceStates)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (state is not null)
        {
            grainState.State = (T)(object)state;
            grainState.ETag = state.ETag;
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.GetGuidKey();
        var state = (WorkflowInstanceState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.WorkflowInstances
            .Include(e => e.Entries)
            .Include(e => e.VariableStates)
            .Include(e => e.ConditionSequenceStates)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (existing is null)
        {
            if (grainState.ETag is not null)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', but no record exists");

            db.WorkflowInstances.Add(state);
            db.Entry(state).Property(s => s.Id).CurrentValue = id;
            db.Entry(state).Property(s => s.ETag).CurrentValue = newETag;

            // Explicitly set FK on child entities (EF fixup may not cascade
            // when the principal key is changed via Property().CurrentValue)
            foreach (var entry in state.Entries)
                db.Entry(entry).Property(e => e.WorkflowInstanceId).CurrentValue = id;
            foreach (var vs in state.VariableStates)
                db.Entry(vs).Property(v => v.WorkflowInstanceId).CurrentValue = id;
            foreach (var cs in state.ConditionSequenceStates)
                db.Entry(cs).Property(c => c.WorkflowInstanceId).CurrentValue = id;
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            // Update root scalars, then override ETag
            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.Id).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;

            // Diff child collections
            DiffEntries(db, existing, state, id);
            DiffVariableStates(db, existing, state, id);
            DiffConditionSequenceStates(db, existing, state, id);
        }

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.GetGuidKey();
        var existing = await db.WorkflowInstances.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.WorkflowInstances.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private static void DiffEntries(
        FleanCommandDbContext db,
        WorkflowInstanceState existing,
        WorkflowInstanceState state,
        Guid workflowInstanceId)
    {
        var existingById = existing.Entries.ToDictionary(e => e.ActivityInstanceId);
        var newIds = state.Entries.Select(e => e.ActivityInstanceId).ToHashSet();

        foreach (var entry in existing.Entries.Where(e => !newIds.Contains(e.ActivityInstanceId)).ToList())
            db.WorkflowActivityInstanceEntries.Remove(entry);

        foreach (var entry in state.Entries)
        {
            if (existingById.TryGetValue(entry.ActivityInstanceId, out var existingEntry))
            {
                db.Entry(existingEntry).CurrentValues.SetValues(entry);
                db.Entry(existingEntry).Property(e => e.WorkflowInstanceId).IsModified = false;
            }
            else
            {
                db.WorkflowActivityInstanceEntries.Add(entry);
                db.Entry(entry).Property(e => e.WorkflowInstanceId).CurrentValue = workflowInstanceId;
            }
        }
    }

    private static void DiffVariableStates(
        FleanCommandDbContext db,
        WorkflowInstanceState existing,
        WorkflowInstanceState state,
        Guid workflowInstanceId)
    {
        var existingById = existing.VariableStates.ToDictionary(v => v.Id);
        var newIds = state.VariableStates.Select(v => v.Id).ToHashSet();

        foreach (var vs in existing.VariableStates.Where(v => !newIds.Contains(v.Id)).ToList())
            db.WorkflowVariableStates.Remove(vs);

        foreach (var vs in state.VariableStates)
        {
            if (existingById.TryGetValue(vs.Id, out var existingVs))
            {
                db.Entry(existingVs).CurrentValues.SetValues(vs);
                db.Entry(existingVs).Property(v => v.WorkflowInstanceId).IsModified = false;
            }
            else
            {
                db.WorkflowVariableStates.Add(vs);
                db.Entry(vs).Property(v => v.WorkflowInstanceId).CurrentValue = workflowInstanceId;
            }
        }
    }

    private static void DiffConditionSequenceStates(
        FleanCommandDbContext db,
        WorkflowInstanceState existing,
        WorkflowInstanceState state,
        Guid workflowInstanceId)
    {
        var existingByKey = existing.ConditionSequenceStates
            .ToDictionary(c => (c.GatewayActivityInstanceId, c.ConditionalSequenceFlowId));

        var newKeys = state.ConditionSequenceStates
            .Select(c => (c.GatewayActivityInstanceId, c.ConditionalSequenceFlowId))
            .ToHashSet();

        foreach (var cs in existing.ConditionSequenceStates
            .Where(c => !newKeys.Contains((c.GatewayActivityInstanceId, c.ConditionalSequenceFlowId))).ToList())
            db.WorkflowConditionSequenceStates.Remove(cs);

        foreach (var cs in state.ConditionSequenceStates)
        {
            var key = (cs.GatewayActivityInstanceId, cs.ConditionalSequenceFlowId);
            if (existingByKey.TryGetValue(key, out var existingCs))
            {
                db.Entry(existingCs).CurrentValues.SetValues(cs);
                db.Entry(existingCs).Property(c => c.WorkflowInstanceId).IsModified = false;
            }
            else
            {
                db.WorkflowConditionSequenceStates.Add(cs);
                db.Entry(cs).Property(c => c.WorkflowInstanceId).CurrentValue = workflowInstanceId;
            }
        }
    }
}
