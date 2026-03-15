using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreUserTaskGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreUserTaskGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId.Key.ToString()!);

        var state = await db.UserTasks.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ActivityInstanceId == id);

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
        var id = Guid.Parse(grainId.Key.ToString()!);
        var state = (UserTaskState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.UserTasks.FindAsync(id);

        if (existing is null)
        {
            state.ActivityInstanceId = id;
            state.ETag = newETag;
            db.UserTasks.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.ActivityInstanceId).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;
        }

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId.Key.ToString()!);
        var existing = await db.UserTasks.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.UserTasks.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
