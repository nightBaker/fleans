namespace Fleans.Application.Projections;

/// <summary>
/// How a silo's <see cref="ProjectionBatcher"/> groups read-model projections into batches
/// (bound from <c>Fleans:Projector</c>, #861). See <c>docs/conventions/persistence.md</c>.
/// </summary>
public sealed class ProjectorOptions
{
    public const string SectionName = "Fleans:Projector";

    /// <summary>Upper bound of <see cref="MaxBatchSize"/>: keeps one upsert's parameters well inside every provider's limit.</summary>
    public const int MaxBatchSizeLimit = 1000;

    /// <summary>
    /// The most projections written in one batch — one upsert statement for the running instances
    /// and one <c>SaveChanges</c> for the completed ones. Default 64, 1..1000. 1 turns batching off.
    /// </summary>
    public int MaxBatchSize { get; set; } = 64;

    /// <summary>
    /// How long a batch waits for more projections after its first one, in milliseconds, before it
    /// is written anyway. Bounds the latency batching adds when the silo is quiet; under load,
    /// batches fill before it runs out. Default 20; 0 writes whatever is queued at once.
    /// </summary>
    public int MaxBatchDelayMs { get; set; } = 20;

    /// <summary>
    /// Batches a silo writes at the same time. Default 4. While all are in flight, new projections
    /// queue — up to <see cref="MaxBatchSize"/> × this many; past that a projector waits to enqueue,
    /// which holds back its stream delivery (back-pressure).
    /// </summary>
    public int MaxConcurrentBatches { get; set; } = 4;

    /// <summary>Upper bound of <see cref="BucketCount"/>.</summary>
    public const int MaxBucketCount = 4096;

    /// <summary>
    /// How many projectors the cluster's instances are spread over, by a stable hash of the instance
    /// id (#1011). Each bucket is one <see cref="WorkflowInstanceProjector"/> on its own
    /// <c>InstanceAdvanced</c> stream: it projects its instances one notification at a time, so this
    /// is the cluster's projection parallelism, and a burst of completions activates nothing new.
    /// Only the publishing (core) silos read it. Changing it needs no migration: a notification queued
    /// under the old count is still projected by its old bucket, and the read model's version guard
    /// makes their order irrelevant. Default 64, 1..4096.
    /// </summary>
    public int BucketCount { get; set; } = 64;

    /// <summary>The projector bucket of <paramref name="instanceId"/>.</summary>
    public int BucketFor(Guid instanceId) => SignalCorrelationOptions.BucketFor(instanceId, BucketCount);

    /// <summary>Upper bound of <see cref="MaxConcurrentReplays"/>.</summary>
    public const int MaxConcurrentReplaysLimit = 256;

    /// <summary>
    /// Journal reads (snapshot plus tail, replayed) the silo's projectors run at the same time
    /// (#1011). Default 8, 1..256. Keep it below the journal connection pool size: past the pool,
    /// reads wait for a connection and fail after its timeout. See <see cref="ProjectionReplaySlots"/>.
    /// </summary>
    public int MaxConcurrentReplays { get; set; } = 8;
}
