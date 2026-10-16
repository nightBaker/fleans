using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Projects workflow instance state to the read model (the WorkflowInstances tables) queried by
/// IWorkflowQueryService. Write-only from the grain's point of view: state is rebuilt from the
/// event store, never from the projection.
/// </summary>
public interface IWorkflowStateProjection
{
    /// <summary>The number of events the projected row reflects, or <c>0</c> when there is none.</summary>
    Task<int> ReadVersionAsync(string grainId);

    /// <summary>
    /// Writes <paramref name="state"/>, the state after the first <paramref name="version"/> events.
    /// Only collections changed since the last write are diffed, unless <paramref name="rebuild"/> is
    /// set — then every collection is rewritten (catch-up after a missed write).
    /// </summary>
    Task WriteAsync(string grainId, WorkflowInstanceState state, int version, bool rebuild = false);
}
