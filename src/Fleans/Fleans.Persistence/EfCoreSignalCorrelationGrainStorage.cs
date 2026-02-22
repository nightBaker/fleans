using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreSignalCorrelationGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreSignalCorrelationGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = await db.SignalCorrelations
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
        var state = (SignalCorrelationState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.SignalCorrelations
            .Include(e => e.Subscriptions)
            .FirstOrDefaultAsync(e => e.Key == id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.SignalCorrelations.Add(state);
            foreach (var sub in state.Subscriptions)
                db.Entry(sub).Property(s => s.SignalName).CurrentValue = id;
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
        var existing = await db.SignalCorrelations.FindAsync(id);
        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");
            db.SignalCorrelations.Remove(existing);
            await db.SaveChangesAsync();
        }
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private static void DiffSubscriptions(
        FleanCommandDbContext db,
        SignalCorrelationState existing,
        SignalCorrelationState state,
        string signalName)
    {
        var existingByKey = existing.Subscriptions
            .ToDictionary(s => (s.WorkflowInstanceId, s.ActivityId));
        var newKeys = state.Subscriptions
            .Select(s => (s.WorkflowInstanceId, s.ActivityId))
            .ToHashSet();

        foreach (var sub in existing.Subscriptions.Where(s => !newKeys.Contains((s.WorkflowInstanceId, s.ActivityId))).ToList())
            db.SignalSubscriptions.Remove(sub);

        foreach (var sub in state.Subscriptions)
        {
            var key = (sub.WorkflowInstanceId, sub.ActivityId);
            if (existingByKey.TryGetValue(key, out var existingSub))
            {
                db.Entry(existingSub).CurrentValues.SetValues(sub);
                db.Entry(existingSub).Property(s => s.SignalName).IsModified = false;
            }
            else
            {
                db.SignalSubscriptions.Add(sub);
                db.Entry(sub).Property(s => s.SignalName).CurrentValue = signalName;
            }
        }
    }
}
