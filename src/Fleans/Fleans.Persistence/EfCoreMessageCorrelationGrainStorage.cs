using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreMessageCorrelationGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreMessageCorrelationGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();

        var state = await db.MessageCorrelations.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == id);

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
        var id = grainId.Key.ToString();
        var state = (MessageCorrelationState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.MessageCorrelations.FindAsync(id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.MessageCorrelations.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.Key).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;
        }

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.MessageCorrelations.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.MessageCorrelations.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
