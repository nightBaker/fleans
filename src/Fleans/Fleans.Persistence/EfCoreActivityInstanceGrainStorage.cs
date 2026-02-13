using Fleans.Domain.States;
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
        var entity = await db.ActivityInstances.FindAsync(id);

        if (entity is not null)
        {
            db.Entry(entity).State = EntityState.Detached;
            grainState.State = (T)(object)entity;
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

            state.Id = id;
            state.ETag = newETag;
            db.ActivityInstances.Add(state);
            try
            {
                await db.SaveChangesAsync();
            }
            finally
            {
                db.Entry(state).State = EntityState.Detached;
            }
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            state.Id = id;
            db.Entry(existing).CurrentValues.SetValues(state);
            existing.ETag = newETag;
            db.Entry(existing).Reference(e => e.ErrorState).CurrentValue = state.ErrorState;
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
}
