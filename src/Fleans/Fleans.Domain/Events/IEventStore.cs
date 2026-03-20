using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Abstraction for event and snapshot persistence.
/// Used by the WorkflowInstance grain's ICustomStorageInterface implementation.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Loads the latest snapshot for a grain, or returns (null, 0) if none exists.
    /// </summary>
    Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(string grainId);

    /// <summary>
    /// Loads all events for a grain after the given version, ordered by version.
    /// </summary>
    Task<IReadOnlyList<IDomainEvent>> ReadEventsAsync(string grainId, int afterVersion);

    /// <summary>
    /// Appends events starting at the given version.
    /// Returns false if a version conflict occurs (unique constraint violation).
    /// </summary>
    Task<bool> AppendEventsAsync(string grainId, IReadOnlyList<IDomainEvent> events, int startVersion);

    /// <summary>
    /// Upserts a snapshot for the given grain at the specified version.
    /// </summary>
    Task WriteSnapshotAsync(string grainId, int version, WorkflowInstanceState state);
}
