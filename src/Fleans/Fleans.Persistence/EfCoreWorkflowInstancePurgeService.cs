using Fleans.Application;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleans.Persistence;

/// <summary>
/// Retention purge (#862). Selects finished instances from the read model, deletes their journal
/// and snapshot through <see cref="IEventStore.DeleteInstanceAsync"/> — one instance at a time, by
/// <c>InstanceId</c>, the journal's only access path — and then their read-model rows in one
/// transaction per batch. Journal first: if the purge stops in between, the read-model row is still
/// there to select the instance again, and deleting an already-deleted journal is a no-op.
/// </summary>
public partial class EfCoreWorkflowInstancePurgeService : IWorkflowInstancePurgeService
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;
    private readonly IEventStore _eventStore;
    private readonly ILogger<EfCoreWorkflowInstancePurgeService> _logger;
    private readonly TimeProvider _time;

    public EfCoreWorkflowInstancePurgeService(
        IDbContextFactory<FleanCommandDbContext> dbContextFactory,
        IEventStore eventStore,
        ILogger<EfCoreWorkflowInstancePurgeService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbContextFactory = dbContextFactory;
        _eventStore = eventStore;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowInstancePurgeResult> PurgeCompletedAsync(
        DateTimeOffset completedBefore,
        int batchSize = IWorkflowInstancePurgeService.DefaultBatchSize,
        int maxInstances = IWorkflowInstancePurgeService.DefaultMaxInstances,
        CancellationToken cancellationToken = default)
    {
        // Validate and purge instances first. Deduplication expiry has its own fixed 24-hour
        // window, independent of the operator's instance-retention cutoff (#977).
        var instances = await PurgeCompletedInstancesAsync(completedBefore, batchSize, maxInstances, cancellationToken);
        var expiredBefore = (_time.GetUtcNow() - MessageDeduplicationState.Window).ToUniversalTime();
        var (deleted, hasMore) = await PurgeExpiredMessageDeduplicationsAsync(
            expiredBefore, batchSize, maxInstances, cancellationToken);
        LogMessageDeduplicationsPurged(deleted, hasMore);
        return instances with
        {
            HasMore = instances.HasMore || hasMore,
            PurgedMessageDeduplicationsCount = deleted
        };
    }

    private async Task<WorkflowInstancePurgeResult> PurgeCompletedInstancesAsync(
        DateTimeOffset completedBefore, int batchSize, int maxInstances, CancellationToken cancellationToken)
    {
        var latestAllowed = _time.GetUtcNow() - IWorkflowInstancePurgeService.MinimumAge;
        if (completedBefore > latestAllowed)
            throw new ArgumentOutOfRangeException(nameof(completedBefore), completedBefore,
                $"completedBefore must be at least {IWorkflowInstancePurgeService.MinimumAge.TotalMinutes:0} minutes in the past " +
                "(an instance that finished recently may still be active in memory).");
        if (batchSize is < 1 or > IWorkflowInstancePurgeService.MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize,
                $"batchSize must be between 1 and {IWorkflowInstancePurgeService.MaxBatchSize}.");
        if (maxInstances < 1)
            throw new ArgumentOutOfRangeException(nameof(maxInstances), maxInstances, "maxInstances must be at least 1.");

        // SQLite stores CompletedAt as an ISO-8601 string written in UTC; compare in UTC too.
        var cutoff = completedBefore.ToUniversalTime();
        LogPurgeStarted(cutoff, batchSize, maxInstances);

        var purged = 0;
        while (purged < maxInstances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(batchSize, maxInstances - purged);

            List<Guid> ids;
            await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
            {
                ids = await Candidates(db, cutoff)
                    .OrderBy(i => i.CompletedAt)
                    .Select(i => i.Id)
                    .Take(take)
                    .ToListAsync(cancellationToken);
            }

            if (ids.Count == 0)
                return Finish(purged, hasMore: false);

            foreach (var id in ids)
                await _eventStore.DeleteInstanceAsync(id);
            await DeleteReadModelAsync(ids, cancellationToken);

            purged += ids.Count;
            LogBatchPurged(ids.Count, purged);

            if (ids.Count < take)
                return Finish(purged, hasMore: false);
        }

        await using var check = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return Finish(purged, await Candidates(check, cutoff).AnyAsync(cancellationToken));
    }

    private async Task<(int Deleted, bool HasMore)> PurgeExpiredMessageDeduplicationsAsync(
        DateTimeOffset cutoff, int batchSize, int maxRecords, CancellationToken cancellationToken)
    {
        var deleted = 0;
        // Bound examined keys as well as deletions: a concurrent purge or refreshed record
        // can make a selected batch delete fewer rows than expected.
        for (var examined = 0; examined < maxRecords;)
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var keys = await db.MessageDeduplications.Where(r => r.ReceivedAt <= cutoff)
                .OrderBy(r => r.ReceivedAt).ThenBy(r => r.Key)
                .Select(r => r.Key).Take(Math.Min(batchSize, maxRecords - examined))
                .ToListAsync(cancellationToken);
            if (keys.Count == 0)
                return (deleted, false);

            // Check expiry again in the DELETE, not just the SELECT. A send may have
            // reused the expired key and refreshed ReceivedAt between the two statements.
            deleted += await db.MessageDeduplications
                .Where(r => keys.Contains(r.Key) && r.ReceivedAt <= cutoff)
                .ExecuteDeleteAsync(cancellationToken);
            examined += keys.Count;
        }

        await using var check = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return (deleted, await check.MessageDeduplications.AnyAsync(r => r.ReceivedAt <= cutoff, cancellationToken));
    }

    /// <summary>
    /// Finished before the cutoff (completed, cancelled and failed all set <c>IsCompleted</c>), and
    /// not the child of a running instance — its parent may still read it. A child whose parent is
    /// gone (already purged) is a candidate.
    /// </summary>
    private static IQueryable<WorkflowInstanceState> Candidates(FleanCommandDbContext db, DateTimeOffset cutoff) =>
        db.WorkflowInstances.Where(i =>
            i.IsCompleted
            && i.CompletedAt < cutoff
            && (i.ParentWorkflowInstanceId == null
                || !db.WorkflowInstances.Any(p => p.Id == i.ParentWorkflowInstanceId && !p.IsCompleted)));

    /// <summary>Children first, then the instance rows, so no foreign key is ever left dangling.</summary>
    private async Task DeleteReadModelAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.WorkflowActivityInstanceEntries.Where(e => ids.Contains(e.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowVariableStates.Where(v => ids.Contains(v.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowConditionSequenceStates.Where(c => ids.Contains(c.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
        await db.GatewayForks.Where(f => ids.Contains(f.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
        await db.ComplexGatewayJoinStates.Where(j => ids.Contains(j.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
        await db.Set<TimerCycleTrackingState>().Where(t => ids.Contains(t.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
        await db.UserTasks.Where(t => ids.Contains(t.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowInstances.Where(i => ids.Contains(i.Id)).ExecuteDeleteAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    private WorkflowInstancePurgeResult Finish(int purged, bool hasMore)
    {
        LogPurgeFinished(purged, hasMore);
        return new WorkflowInstancePurgeResult(purged, hasMore);
    }

    [LoggerMessage(EventId = 14100, Level = LogLevel.Information,
        Message = "Purging workflow instances finished before {CompletedBefore:O} (batch size {BatchSize}, at most {MaxInstances})")]
    private partial void LogPurgeStarted(DateTimeOffset completedBefore, int batchSize, int maxInstances);

    [LoggerMessage(EventId = 14101, Level = LogLevel.Information,
        Message = "Purged {BatchCount} workflow instances (journal, snapshot, read model); {PurgedCount} so far")]
    private partial void LogBatchPurged(int batchCount, int purgedCount);

    [LoggerMessage(EventId = 14102, Level = LogLevel.Information,
        Message = "Workflow instance purge finished: {PurgedCount} purged, more remaining: {HasMore}")]
    private partial void LogPurgeFinished(int purgedCount, bool hasMore);

    [LoggerMessage(EventId = 14103, Level = LogLevel.Information,
        Message = "Purged {PurgedCount} expired message-deduplication records; more remaining: {HasMore}")]
    private partial void LogMessageDeduplicationsPurged(int purgedCount, bool hasMore);
}
