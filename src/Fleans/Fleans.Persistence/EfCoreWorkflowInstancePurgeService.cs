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

    public EfCoreWorkflowInstancePurgeService(
        IDbContextFactory<FleanCommandDbContext> dbContextFactory,
        IEventStore eventStore,
        ILogger<EfCoreWorkflowInstancePurgeService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _eventStore = eventStore;
        _logger = logger;
    }

    public async Task<WorkflowInstancePurgeResult> PurgeCompletedAsync(
        DateTimeOffset completedBefore,
        int batchSize = IWorkflowInstancePurgeService.DefaultBatchSize,
        int maxInstances = IWorkflowInstancePurgeService.DefaultMaxInstances,
        CancellationToken cancellationToken = default)
    {
        var latestAllowed = DateTimeOffset.UtcNow - IWorkflowInstancePurgeService.MinimumAge;
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
}
