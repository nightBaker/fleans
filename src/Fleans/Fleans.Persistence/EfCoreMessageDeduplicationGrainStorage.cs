using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

/// <summary>
/// One <c>MessageDeduplications</c> row per <c>MessageDeduplicationGrain</c> — message name +
/// idempotency key (#743) — read and written by that key alone, ETag-checked like
/// <see cref="EfCoreDeploymentRequestGrainStorage"/>.
/// </summary>
public class EfCoreMessageDeduplicationGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreMessageDeduplicationGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var key = grainId.Key.ToString()!;

        var state = await db.MessageDeduplications.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == key);

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
        var key = grainId.Key.ToString()!;
        var state = (MessageDeduplicationState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");
        state.Key = key;

        var existing = await db.MessageDeduplications.FindAsync(key);

        if (existing is null)
        {
            state.ETag = newETag;
            db.MessageDeduplications.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;
        }

        await db.SaveChangesAsync();

        state.ETag = newETag;
        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var key = grainId.Key.ToString()!;
        var existing = await db.MessageDeduplications.FindAsync(key);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.MessageDeduplications.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
