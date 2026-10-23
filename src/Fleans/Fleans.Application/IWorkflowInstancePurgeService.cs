namespace Fleans.Application;

/// <summary>
/// Retention (#862): deletes everything stored for workflow instances that finished — completed,
/// cancelled or failed — before a cutoff: journal, snapshot and read-model rows. Runbook:
/// <c>docs/runbooks/instance-retention.md</c>.
/// </summary>
public interface IWorkflowInstancePurgeService
{
    /// <summary>Instances finished more recently than this are never purged (see the runbook).</summary>
    static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    const int DefaultBatchSize = 500;
    const int MaxBatchSize = 5000;
    const int DefaultMaxInstances = 10_000;

    /// <summary>
    /// Purges up to <paramref name="maxInstances"/> instances that finished before
    /// <paramref name="completedBefore"/>, oldest first, <paramref name="batchSize"/> at a time.
    /// Candidates are selected from the read model; a child instance is kept while its parent is
    /// still running. Each batch deletes the journal and snapshot of every instance first, then
    /// its read-model rows, so an interrupted purge leaves the rows behind and the next call
    /// finishes them. Throws <see cref="ArgumentOutOfRangeException"/> when
    /// <paramref name="completedBefore"/> is less than <see cref="MinimumAge"/> ago or a size is
    /// out of range.
    /// </summary>
    Task<WorkflowInstancePurgeResult> PurgeCompletedAsync(
        DateTimeOffset completedBefore,
        int batchSize = DefaultBatchSize,
        int maxInstances = DefaultMaxInstances,
        CancellationToken cancellationToken = default);
}

/// <param name="PurgedCount">Instances deleted by this call.</param>
/// <param name="HasMore">More instances match the cutoff — call again.</param>
public record WorkflowInstancePurgeResult(int PurgedCount, bool HasMore);
