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
    /// <summary>The instance's stored row, or <c>null</c> when there is none.</summary>
    Task<WorkflowInstanceRow?> ReadRowAsync(Guid instanceId);

    /// <summary>Writes a running instance's row; its details stay with the grain.</summary>
    Task WriteRowAsync(Guid instanceId, WorkflowInstanceRow row);

    /// <summary>Writes a completed instance's row together with its details.</summary>
    Task WriteCompletedAsync(Guid instanceId, WorkflowInstanceState state);
}
