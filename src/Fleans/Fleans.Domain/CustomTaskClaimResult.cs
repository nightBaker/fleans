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
