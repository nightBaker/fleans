using Fleans.Domain;
using Fleans.Domain.States;
using Fleans.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreActivityInstanceGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<GrainStateDbContext> _dbContextFactory;

    public EfCoreActivityInstanceGrainStorage(IDbContextFactory<GrainStateDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.GetGuidKey();
        var entity = await db.ActivityInstances.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);

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
        var state = (ActivityInstanceState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.ActivityInstances.FindAsync(id);

        if (existing is null)
        {
            if (grainState.ETag is not null)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', but no record exists");

            var entity = MapToEntity(state, id);
            entity.ETag = newETag;
            db.ActivityInstances.Add(entity);
            await db.SaveChangesAsync();
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            existing.ActivityId = state.ActivityId;
            existing.ActivityType = state.ActivityType;
            existing.IsExecuting = state.IsExecuting;
            existing.IsCompleted = state.IsCompleted;
            existing.VariablesId = state.VariablesId;
            existing.ErrorCode = state.ErrorState?.Code;
            existing.ErrorMessage = state.ErrorState?.Message;
            existing.CreatedAt = state.CreatedAt;
            existing.ExecutionStartedAt = state.ExecutionStartedAt;
            existing.CompletedAt = state.CompletedAt;
            existing.ETag = newETag;
            await db.SaveChangesAsync();
        }

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.GetGuidKey();
        var existing = await db.ActivityInstances.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.ActivityInstances.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private static ActivityInstanceEntity MapToEntity(ActivityInstanceState state, Guid id) =>
        new()
        {
            Id = id,
            ActivityId = state.ActivityId,
            ActivityType = state.ActivityType,
            IsExecuting = state.IsExecuting,
            IsCompleted = state.IsCompleted,
            VariablesId = state.VariablesId,
            ErrorCode = state.ErrorState?.Code,
            ErrorMessage = state.ErrorState?.Message,
            CreatedAt = state.CreatedAt,
            ExecutionStartedAt = state.ExecutionStartedAt,
            CompletedAt = state.CompletedAt,
        };

    private static ActivityInstanceState MapToDomain(ActivityInstanceEntity entity)
    {
        var state = new ActivityInstanceState
        {
            ActivityId = entity.ActivityId,
            ActivityType = entity.ActivityType,
            IsExecuting = entity.IsExecuting,
            IsCompleted = entity.IsCompleted,
            VariablesId = entity.VariablesId,
            ErrorState = entity.ErrorCode is not null
                ? new ActivityErrorState(entity.ErrorCode.Value, entity.ErrorMessage!)
                : null,
            CreatedAt = entity.CreatedAt,
            ExecutionStartedAt = entity.ExecutionStartedAt,
            CompletedAt = entity.CompletedAt,
        };

        return state;
    }
}
