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
}
