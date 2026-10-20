using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Projects workflow instance state to the read model (the WorkflowInstances tables) queried by
/// IWorkflowQueryService. Write-only from the grain's point of view: state is rebuilt from the
/// event store, never from the projection.
/// </summary>
/// <remarks>
/// A running instance projects only its WorkflowInstances row (status, timestamps, business id) —
/// what cross-instance queries need; its details (activities, variables, conditions) are served by
/// the grain. Once <see cref="WorkflowInstanceState.IsCompleted"/>, the details are written too, so
/// a finished instance is read without activating it.
/// </remarks>
public interface IWorkflowStateProjection
{
    /// <summary>The number of events the projected row reflects, or <c>0</c> when there is none.</summary>
    Task<int> ReadVersionAsync(string grainId);

    /// <summary>Writes <paramref name="state"/>, the state after the first <paramref name="version"/> events.</summary>
    Task WriteAsync(string grainId, WorkflowInstanceState state, int version);
}
