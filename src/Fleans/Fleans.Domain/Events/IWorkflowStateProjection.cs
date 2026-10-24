using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Projects workflow instance state to the read model (the WorkflowInstances tables) queried by
/// IWorkflowQueryService. Written only by the read-model projector, from state it rebuilt from the
/// journal (#861); never loaded back as grain state.
/// </summary>
/// <remarks>
/// A running instance projects only its WorkflowInstances row (<see cref="WorkflowInstanceRow"/>:
/// status, timestamps, business id) — what cross-instance queries need; its details (activities,
/// variables, conditions) are served by the grain. Once <see cref="WorkflowInstanceState.IsCompleted"/>,
/// the details are written too, so a finished instance is read without activating it.
/// </remarks>
public interface IWorkflowStateProjection
{
    /// <summary>The projected row, or <c>null</c> when there is none.</summary>
    Task<WorkflowInstanceRow?> ReadRowAsync(string grainId);

    /// <summary>
    /// Writes the row of <paramref name="state"/> — the state after the first
    /// <paramref name="version"/> journal events — and its details once it is completed. The row
    /// stores the version it reflects; a write whose version is not newer than the stored one
    /// changes nothing, so a repeated or late write is harmless.
    /// </summary>
    Task WriteAsync(string grainId, WorkflowInstanceState state, int version);

    /// <summary>
    /// Writes many instances' projections as one set-based statement, with the same per-row
    /// semantics as <see cref="WriteAsync"/>: each row lands only past its stored version. When the
    /// batch holds several projections of one instance, the newest wins. It either lands or
    /// throws for the whole batch — a caller must treat a throw as "none of these is durable".
    /// </summary>
    Task WriteBatchAsync(IReadOnlyList<ProjectionWrite> writes);
}

/// <summary>One projection of a batch: <paramref name="State"/> after the first <paramref name="Version"/> journal events.</summary>
public sealed record ProjectionWrite(string GrainId, WorkflowInstanceState State, int Version);
