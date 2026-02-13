using System.Dynamic;
using Fleans.Domain.States;
using Fleans.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreWorkflowInstanceGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<GrainStateDbContext> _dbContextFactory;

    public EfCoreWorkflowInstanceGrainStorage(IDbContextFactory<GrainStateDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.GetGuidKey();

        var entity = await db.WorkflowInstances
            .Include(e => e.Entries)
            .Include(e => e.VariableStates)
            .Include(e => e.ConditionSequenceStates)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id);

        if (entity is not null)
        {
            var state = MapToDomain(entity);
            grainState.State = (T)(object)state;
            grainState.ETag = entity.ETag;
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

            var entity = MapToEntity(state, id);
            entity.ETag = newETag;
            db.WorkflowInstances.Add(entity);
            await db.SaveChangesAsync();
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            // Update scalars
            existing.IsStarted = state.IsStarted;
            existing.IsCompleted = state.IsCompleted;
            existing.CreatedAt = state.CreatedAt;
            existing.ExecutionStartedAt = state.ExecutionStartedAt;
            existing.CompletedAt = state.CompletedAt;
            existing.ETag = newETag;

            // Diff entries
            DiffEntries(db, existing, state, id);

            // Diff variable states
            DiffVariableStates(db, existing, state, id);

            // Diff condition sequence states
            DiffConditionSequenceStates(db, existing, state, id);

            await db.SaveChangesAsync();
        }

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

    private static WorkflowInstanceEntity MapToEntity(WorkflowInstanceState state, Guid id)
    {
        var entity = new WorkflowInstanceEntity
        {
            Id = id,
            IsStarted = state.IsStarted,
            IsCompleted = state.IsCompleted,
            CreatedAt = state.CreatedAt,
            ExecutionStartedAt = state.ExecutionStartedAt,
            CompletedAt = state.CompletedAt,
        };

        foreach (var entry in state.ActiveActivities)
        {
            entity.Entries.Add(new ActivityInstanceEntryEntity
            {
                ActivityInstanceId = entry.ActivityInstanceId,
                ActivityId = entry.ActivityId,
                WorkflowInstanceId = id,
                IsCompleted = false,
            });
        }

        foreach (var entry in state.CompletedActivities)
        {
            entity.Entries.Add(new ActivityInstanceEntryEntity
            {
                ActivityInstanceId = entry.ActivityInstanceId,
                ActivityId = entry.ActivityId,
                WorkflowInstanceId = id,
                IsCompleted = true,
            });
        }

        foreach (var kvp in state.VariableStates)
        {
            entity.VariableStates.Add(new WorkflowVariablesEntity
            {
                Id = kvp.Key,
                WorkflowInstanceId = id,
                Variables = kvp.Value.Variables,
            });
        }

        foreach (var kvp in state.ConditionSequenceStates)
        {
            foreach (var cs in kvp.Value)
            {
                entity.ConditionSequenceStates.Add(new ConditionSequenceEntity
                {
                    ConditionalSequenceFlowId = cs.ConditionalSequenceFlowId,
                    Result = cs.Result,
                    IsEvaluated = cs.IsEvaluated,
                    WorkflowInstanceId = id,
                    GatewayActivityInstanceId = kvp.Key,
                });
            }
        }

        return entity;
    }

    private static WorkflowInstanceState MapToDomain(WorkflowInstanceEntity entity)
    {
        var state = new WorkflowInstanceState();

        state.InstanceId = entity.Id;
        state.CreatedAt = entity.CreatedAt;
        state.ExecutionStartedAt = entity.ExecutionStartedAt;
        state.CompletedAt = entity.CompletedAt;

        if (entity.IsStarted) state.Start();
        if (entity.IsCompleted) state.Complete();

        // Split entries by IsCompleted
        var activeEntries = entity.Entries
            .Where(e => !e.IsCompleted)
            .Select(e => new ActivityInstanceEntry(e.ActivityInstanceId, e.ActivityId));
        state.AddActiveActivities(activeEntries);

        var completedEntries = entity.Entries
            .Where(e => e.IsCompleted)
            .Select(e => new ActivityInstanceEntry(e.ActivityInstanceId, e.ActivityId));
        state.AddCompletedActivities(completedEntries);

        // Reconstruct VariableStates — keyed by stored Id, merge in persisted variables
        foreach (var ve in entity.VariableStates)
        {
            var vs = new WorkflowVariablesState();
            vs.Merge(ve.Variables);
            state.VariableStates[ve.Id] = vs;
        }

        // Reconstruct ConditionSequenceStates grouped by GatewayActivityInstanceId
        foreach (var group in entity.ConditionSequenceStates.GroupBy(c => c.GatewayActivityInstanceId))
        {
            var sequenceFlowIds = group.Select(c => c.ConditionalSequenceFlowId).ToArray();
            state.AddConditionSequenceStates(group.Key, sequenceFlowIds);

            // Now apply the evaluated results
            foreach (var ce in group.Where(c => c.IsEvaluated))
            {
                state.SetConditionSequenceResult(group.Key, ce.ConditionalSequenceFlowId, ce.Result);
            }
        }

        return state;
    }

    private static void DiffEntries(
        GrainStateDbContext db,
        WorkflowInstanceEntity existing,
        WorkflowInstanceState state,
        Guid workflowInstanceId)
    {
        var existingById = existing.Entries.ToDictionary(e => e.ActivityInstanceId);

        var allNewEntries = state.ActiveActivities
            .Select(e => (e.ActivityInstanceId, e.ActivityId, IsCompleted: false))
            .Concat(state.CompletedActivities
                .Select(e => (e.ActivityInstanceId, e.ActivityId, IsCompleted: true)))
            .ToList();

        var newIds = allNewEntries.Select(e => e.ActivityInstanceId).ToHashSet();

        // Delete removed
        foreach (var entry in existing.Entries.Where(e => !newIds.Contains(e.ActivityInstanceId)).ToList())
        {
            db.WorkflowActivityInstanceEntries.Remove(entry);
            existing.Entries.Remove(entry);
        }

        // Insert new or update existing
        foreach (var (activityInstanceId, activityId, isCompleted) in allNewEntries)
        {
            if (existingById.TryGetValue(activityInstanceId, out var existingEntry))
            {
                if (existingEntry.IsCompleted != isCompleted)
                    existingEntry.IsCompleted = isCompleted;
            }
            else
            {
                var newEntry = new ActivityInstanceEntryEntity
                {
                    ActivityInstanceId = activityInstanceId,
                    ActivityId = activityId,
                    WorkflowInstanceId = workflowInstanceId,
                    IsCompleted = isCompleted,
                };
                db.WorkflowActivityInstanceEntries.Add(newEntry);
            }
        }
    }

    private static void DiffVariableStates(
        GrainStateDbContext db,
        WorkflowInstanceEntity existing,
        WorkflowInstanceState state,
        Guid workflowInstanceId)
    {
        var existingById = existing.VariableStates.ToDictionary(v => v.Id);
        var newVars = state.VariableStates;

        // Delete removed
        foreach (var vs in existing.VariableStates.Where(v => !newVars.ContainsKey(v.Id)).ToList())
        {
            db.WorkflowVariableStates.Remove(vs);
            existing.VariableStates.Remove(vs);
        }

        // Insert new or update existing
        foreach (var kvp in newVars)
        {
            if (existingById.TryGetValue(kvp.Key, out var existingVs))
            {
                var newJson = JsonConvert.SerializeObject(kvp.Value.Variables);
                var existingJson = JsonConvert.SerializeObject(existingVs.Variables);
                if (newJson != existingJson)
                    existingVs.Variables = kvp.Value.Variables;
            }
            else
            {
                db.WorkflowVariableStates.Add(new WorkflowVariablesEntity
                {
                    Id = kvp.Key,
                    WorkflowInstanceId = workflowInstanceId,
                    Variables = kvp.Value.Variables,
                });
            }
        }
    }

    private static void DiffConditionSequenceStates(
        GrainStateDbContext db,
        WorkflowInstanceEntity existing,
        WorkflowInstanceState state,
        Guid workflowInstanceId)
    {
        var existingByKey = existing.ConditionSequenceStates
            .ToDictionary(c => (c.GatewayActivityInstanceId, c.ConditionalSequenceFlowId));

        var allNewConditions = state.ConditionSequenceStates
            .SelectMany(kvp => kvp.Value.Select(cs => (GatewayId: kvp.Key, Condition: cs)))
            .ToList();

        var newKeys = allNewConditions
            .Select(c => (c.GatewayId, c.Condition.ConditionalSequenceFlowId))
            .ToHashSet();

        // Delete removed
        foreach (var cs in existing.ConditionSequenceStates.Where(c => !newKeys.Contains((c.GatewayActivityInstanceId, c.ConditionalSequenceFlowId))).ToList())
        {
            db.WorkflowConditionSequenceStates.Remove(cs);
            existing.ConditionSequenceStates.Remove(cs);
        }

        // Insert new or update existing
        foreach (var (gatewayId, condition) in allNewConditions)
        {
            var key = (gatewayId, condition.ConditionalSequenceFlowId);
            if (existingByKey.TryGetValue(key, out var existingCs))
            {
                if (existingCs.Result != condition.Result || existingCs.IsEvaluated != condition.IsEvaluated)
                {
                    existingCs.Result = condition.Result;
                    existingCs.IsEvaluated = condition.IsEvaluated;
                }
            }
            else
            {
                db.WorkflowConditionSequenceStates.Add(new ConditionSequenceEntity
                {
                    ConditionalSequenceFlowId = condition.ConditionalSequenceFlowId,
                    Result = condition.Result,
                    IsEvaluated = condition.IsEvaluated,
                    WorkflowInstanceId = workflowInstanceId,
                    GatewayActivityInstanceId = gatewayId,
                });
            }
        }
    }
}
