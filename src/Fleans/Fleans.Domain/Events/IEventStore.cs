using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// Abstraction over the event store for JournaledGrain persistence.
/// Implemented by EfCoreEventStore in the Persistence layer.
/// <para>
/// Versions: a grain's version is its event count. Stored events are numbered 1..N; the store
/// assigns the numbers, callers only pass the count they expect to be stored already.
/// </para>
/// <para>
/// Snapshot: the projected state together with the version it covers, written atomically after
/// every append. Replay applies only the events after that version, so it never re-applies an
/// event the snapshot already contains.
/// </para>
/// </summary>
public interface IEventStore
{
    /// <summary>The latest snapshot and the version it covers, or <c>(null, 0)</c>.</summary>
    Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(string grainId);

    /// <summary>Events with a version greater than <paramref name="afterVersion"/>, in order.</summary>
    Task<IReadOnlyList<IDomainEvent>> ReadEventsAsync(string grainId, int afterVersion);

    /// <summary>
    /// Appends <paramref name="events"/> as versions <c>expectedVersion + 1</c>, … . Returns
    /// <c>false</c> when another writer already stored events past <paramref name="expectedVersion"/>.
    /// </summary>
    Task<bool> AppendEventsAsync(string grainId, IReadOnlyList<IDomainEvent> events, int expectedVersion);

    /// <summary>
    /// Writes <paramref name="state"/> to the query store (so IWorkflowQueryService sees it) and
    /// records that it covers the first <paramref name="version"/> events — in one transaction.
    /// Called after every successful append.
    /// </summary>
    Task WriteSnapshotAsync(string grainId, int version, WorkflowInstanceState state);
}
