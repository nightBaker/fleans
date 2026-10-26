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
        var (name, bucket) = KeyOf(grainId);
        var state = await db.SignalCorrelations
            .Include(e => e.Subscriptions)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.SignalName == name && e.Bucket == bucket);
        if (state is not null)
        {
            grainState.State = (T)(object)state;
            grainState.ETag = state.ETag;
            grainState.RecordExists = true;
        }
    }

    /// <summary>
    /// The grain key: integer part = bucket, key extension = signal name.
    /// </summary>
    private static (string SignalName, int Bucket) KeyOf(GrainId grainId)
    {
        var bucket = grainId.GetIntegerKey(out var signalName);
        return (signalName ?? string.Empty, checked((int)bucket));
    }

    /// <summary>
    /// Writes only the change set recorded on <see cref="SignalCorrelationState"/> for one bucket
    /// of a signal name: the parent row is loaded alone for the ETag check, then the broadcast
    /// clear is one set-based delete of the bucket's rows,
    /// removed keys are deleted by key and added subscriptions are inserted. Cost is independent
    /// of how many subscriptions the signal already has (#859). The first write for a signal
    /// (no parent row) inserts every subscription in the state.
    /// </summary>
    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var (name, bucket) = KeyOf(grainId);
        var state = (SignalCorrelationState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        await using var transaction = await db.Database.BeginTransactionAsync();

        var existing = await db.SignalCorrelations
            .FirstOrDefaultAsync(e => e.SignalName == name && e.Bucket == bucket);

        if (existing is null)
        {
            db.SignalCorrelations.Add(new SignalCorrelationState { SignalName = name, Bucket = bucket, ETag = newETag });
            db.SignalSubscriptions.AddRange(state.Subscriptions.Select(s => s with { SignalName = name, Bucket = bucket }));
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            existing.ETag = newETag;

            if (state.SubscriptionsCleared)
                await db.SignalSubscriptions
                    .Where(s => s.SignalName == name && s.Bucket == bucket)
                    .ExecuteDeleteAsync();

            foreach (var (workflowInstanceId, activityId) in state.RemovedSubscriptionKeys)
            {
                await db.SignalSubscriptions
                    .Where(s => s.SignalName == name
                        && s.Bucket == bucket
                        && s.WorkflowInstanceId == workflowInstanceId
                        && s.ActivityId == activityId)
                    .ExecuteDeleteAsync();
            }

            db.SignalSubscriptions.AddRange(state.AddedSubscriptions.Select(s => s with { SignalName = name, Bucket = bucket }));
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        state.SignalName = name;
        state.Bucket = bucket;
        state.ETag = newETag;
        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var (name, bucket) = KeyOf(grainId);
        var existing = await db.SignalCorrelations.FindAsync(name, bucket);
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
