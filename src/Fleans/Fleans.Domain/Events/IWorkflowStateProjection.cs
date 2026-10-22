using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Projects workflow instance state to the read model (the WorkflowInstances tables) queried by
/// IWorkflowQueryService. Write-only from the grain's point of view: state is rebuilt from the
/// event store, never from the projection.
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

    /// <summary>Writes the row of <paramref name="state"/>, and its details once it is completed.</summary>
    Task WriteAsync(string grainId, WorkflowInstanceState state);
}
