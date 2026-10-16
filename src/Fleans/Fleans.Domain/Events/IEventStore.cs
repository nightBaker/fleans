using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// The journal of the event-sourced WorkflowInstance grain, plus its snapshots.
/// Implemented by EfCoreEventStore in the Persistence layer.
/// <para>
/// Versions: a grain's version is its event count. Stored events are numbered 1..N; the store
/// assigns the numbers, callers only pass the count they expect to be stored already.
/// </para>
/// <para>
/// Snapshots are a cache: the full state after the first <c>Version</c> events. Activation loads
/// the latest snapshot and replays the events after it; with no (readable) snapshot it replays the
/// whole journal. The read model is a separate projection (<see cref="IWorkflowStateProjection"/>)
/// and is never loaded back as state.
/// </para>
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// A snapshot is written once at least this many events were appended since the last one.
    /// </summary>
    int SnapshotInterval { get; }

    /// <summary>
    /// The latest snapshot and the version it covers, or <c>(null, 0)</c>. Throws when the snapshot
    /// exists but cannot be read.
    /// </summary>
    Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(string grainId);

    /// <summary>Events with a version greater than <paramref name="afterVersion"/>, in order.</summary>
    Task<IReadOnlyList<IDomainEvent>> ReadEventsAsync(string grainId, int afterVersion);

    /// <summary>
    /// Appends <paramref name="events"/> as versions <c>expectedVersion + 1</c>, … . Returns
    /// <c>false</c> when another writer already stored events past <paramref name="expectedVersion"/>.
    /// </summary>
    Task<bool> AppendEventsAsync(string grainId, IReadOnlyList<IDomainEvent> events, int expectedVersion);

    /// <summary>
    /// Replaces the grain's snapshot with <paramref name="state"/>, which must be exactly the state
    /// after the first <paramref name="version"/> events.
    /// </summary>
    Task WriteSnapshotAsync(string grainId, int version, WorkflowInstanceState state);
}
