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

        var state = await db.MessageCorrelations
            .Include(e => e.Subscriptions)
            .AsNoTracking()
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

        var existing = await db.MessageCorrelations
            .Include(e => e.Subscriptions)
            .FirstOrDefaultAsync(e => e.Key == id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.MessageCorrelations.Add(state);
            foreach (var sub in state.Subscriptions)
                db.Entry(sub).Property(s => s.MessageName).CurrentValue = id;
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.Key).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;

            DiffSubscriptions(db, existing, state, id);
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

    private static void DiffSubscriptions(
        FleanCommandDbContext db,
        MessageCorrelationState existing,
        MessageCorrelationState state,
        string messageName)
    {
        var existingByKey = existing.Subscriptions
            .ToDictionary(s => s.CorrelationKey);
        var newKeys = state.Subscriptions
            .Select(s => s.CorrelationKey)
            .ToHashSet();

        foreach (var sub in existing.Subscriptions.Where(s => !newKeys.Contains(s.CorrelationKey)).ToList())
            db.MessageSubscriptions.Remove(sub);

        foreach (var sub in state.Subscriptions)
        {
            if (existingByKey.TryGetValue(sub.CorrelationKey, out var existingSub))
            {
                db.Entry(existingSub).CurrentValues.SetValues(sub);
                db.Entry(existingSub).Property(s => s.MessageName).IsModified = false;
                db.Entry(existingSub).Property(s => s.CorrelationKey).IsModified = false;
            }
            else
            {
                db.MessageSubscriptions.Add(sub);
                db.Entry(sub).Property(s => s.MessageName).CurrentValue = messageName;
            }
        }
    }
}
