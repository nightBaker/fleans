using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Abstraction for workflow event store operations.
/// Implemented by EfCoreEventStore in the persistence layer.
/// </summary>
public interface IWorkflowEventStore
{
    Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(string grainId);
    Task<IReadOnlyList<IDomainEvent>> ReadEventsAsync(string grainId, int afterVersion);
    Task<bool> AppendEventsAsync(string grainId, IReadOnlyList<IDomainEvent> events, int startVersion);
    Task WriteSnapshotAsync(string grainId, int version, WorkflowInstanceState state);
}
