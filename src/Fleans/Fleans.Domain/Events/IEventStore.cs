using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Abstraction over the event store for JournaledGrain persistence.
/// Implemented by EfCoreEventStore in the Persistence layer.
/// </summary>
public interface IEventStore
{
    Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(string grainId);
    Task<IReadOnlyList<IDomainEvent>> ReadEventsAsync(string grainId, int afterVersion);
    Task<bool> AppendEventsAsync(string grainId, IReadOnlyList<IDomainEvent> events, int startVersion);
    Task WriteSnapshotAsync(string grainId, int version, WorkflowInstanceState state);

    /// <summary>
    /// Projects the current workflow state to the query store so that
    /// IWorkflowQueryService can read up-to-date workflow instance data.
    /// Called after every successful event append.
    /// </summary>
    Task ProjectStateAsync(string grainId, WorkflowInstanceState state);
}
