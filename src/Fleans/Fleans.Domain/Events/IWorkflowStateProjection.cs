using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Projects the current workflow instance state to a read model (e.g., the WorkflowInstances table).
/// The projection doubles as the event store's snapshot, so it is stored together with the
/// version it covers.
/// </summary>
public interface IWorkflowStateProjection
{
    /// <summary>The projected state and the number of events it covers, or <c>(null, 0)</c>.</summary>
    Task<(WorkflowInstanceState? State, int Version)> ReadAsync(string grainId);

    /// <summary>
    /// Writes <paramref name="state"/> together with the number of events it covers, atomically,
    /// so the projected state and the version replay starts from can never disagree.
    /// </summary>
    Task WriteAsync(string grainId, WorkflowInstanceState state, int version);
}
