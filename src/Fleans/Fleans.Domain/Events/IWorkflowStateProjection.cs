using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Projects the current workflow instance state to a read model (e.g., the WorkflowInstances table).
/// The projection doubles as the event store's snapshot, so it is written together with the
/// version it covers.
/// </summary>
public interface IWorkflowStateProjection
{
    Task<WorkflowInstanceState?> ReadAsync(string grainId);

    /// <summary>
    /// Writes <paramref name="state"/> and the snapshot <paramref name="version"/> in one transaction,
    /// so the projected state and the version replay starts from can never disagree.
    /// </summary>
    Task WriteAsync(string grainId, WorkflowInstanceState state, int version);
}
