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

    /// <summary>
    /// Writes only the change set recorded on <see cref="SignalCorrelationState"/>: the parent
    /// row is loaded alone for the ETag check, then the broadcast clear is one set-based delete,
    /// removed keys are deleted by key and added subscriptions are inserted. Cost is independent
    /// of how many subscriptions the signal already has (#859). The first write for a signal
    /// (no parent row) inserts every subscription in the state.
    /// </summary>
    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = (SignalCorrelationState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        await using var transaction = await db.Database.BeginTransactionAsync();

        var existing = await db.SignalCorrelations.FirstOrDefaultAsync(e => e.Key == id);

        if (existing is null)
        {
            db.SignalCorrelations.Add(new SignalCorrelationState { Key = id, ETag = newETag });
            db.SignalSubscriptions.AddRange(state.Subscriptions.Select(s => s with { SignalName = id }));
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            existing.ETag = newETag;

            if (state.SubscriptionsCleared)
                await db.SignalSubscriptions.Where(s => s.SignalName == id).ExecuteDeleteAsync();

            foreach (var (workflowInstanceId, activityId) in state.RemovedSubscriptionKeys)
            {
                await db.SignalSubscriptions
                    .Where(s => s.SignalName == id
                        && s.WorkflowInstanceId == workflowInstanceId
                        && s.ActivityId == activityId)
                    .ExecuteDeleteAsync();
            }

            db.SignalSubscriptions.AddRange(state.AddedSubscriptions.Select(s => s with { SignalName = id }));
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        state.Key = id;
        state.ETag = newETag;
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
}
