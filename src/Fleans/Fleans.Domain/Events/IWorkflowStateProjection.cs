using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Projects the current workflow instance state to a read model (e.g., the WorkflowInstances table).
/// Called by the event store for snapshot persistence using normalized SQL tables.
/// </summary>
public interface IWorkflowStateProjection
{
    Task<WorkflowInstanceState?> ReadAsync(string grainId);
    Task WriteAsync(string grainId, WorkflowInstanceState state);
}
