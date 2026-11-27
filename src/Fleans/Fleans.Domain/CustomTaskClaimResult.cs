namespace Fleans.Domain;

/// <summary>
/// The answer to a dispatcher's claim on a custom task (#818): whether this delivery may hand the
/// task to a worker.
/// </summary>
[GenerateSerializer]
public enum CustomTaskClaimResult
{
    /// <summary>This dispatch holds the task: hand it to a worker.</summary>
    Granted,

    /// <summary>The activity has completed, failed or been cancelled: nothing to run.</summary>
    Finished,

    /// <summary>A worker deferred the task; it waits for a REST report, not for another worker.</summary>
    Deferred,

    /// <summary>Another dispatch holds the task and its lease has not run out: try again later.</summary>
    Held
}

/// <summary>
/// The answer to <c>TakeCustomTask</c>: the claim's result, and the dispatch id that holds the task
/// when it is <see cref="CustomTaskClaimResult.Granted"/> (the reservation the task was published
/// with, or a new id when that reservation was taken already).
/// </summary>
[GenerateSerializer]
public sealed record CustomTaskTake(
    [property: Id(0)] CustomTaskClaimResult Result,
    [property: Id(1)] Guid DispatchId);
