using Fleans.Domain.States;

namespace Fleans.Domain.Events;

/// <summary>
/// The journal of the event-sourced WorkflowInstance grain, plus its snapshots.
/// Implemented by EfCoreEventStore in the Persistence layer.
/// <para>
/// Versions: a grain's version is its event count. Stored events are numbered 1..N; the store
/// assigns the numbers, callers only pass the count they expect to be stored already. Each append
/// is stored as one unit, so the versions a caller sees (and snapshots) always fall on an append
/// boundary.
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
    Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(Guid instanceId);

    /// <summary>Events with a version greater than <paramref name="afterVersion"/>, in order.</summary>
    Task<IReadOnlyList<WorkflowEvent>> ReadEventsAsync(Guid instanceId, int afterVersion);

    /// <summary>
    /// Appends <paramref name="events"/> as one batch, versions <c>expectedVersion + 1</c>, … ,
    /// stored with <paramref name="batchId"/>. Returns <c>true</c> when the batch is stored — also
    /// when a batch with the same <paramref name="batchId"/> is already stored at
    /// <c>expectedVersion + 1</c>: a retry of an append that was stored but reported as failed
    /// (#877) succeeds without appending it again. Returns <c>false</c> when another batch is stored
    /// past <paramref name="expectedVersion"/>. A retry must pass the same events as the first try.
    /// </summary>
    Task<bool> AppendEventsAsync(Guid instanceId, IReadOnlyList<WorkflowEvent> events, int expectedVersion, Guid batchId);

    /// <summary>
    /// Replaces the grain's snapshot with <paramref name="state"/>, which must be exactly the state
    /// after the first <paramref name="version"/> events.
    /// </summary>
    Task WriteSnapshotAsync(Guid instanceId, int version, WorkflowInstanceState state);

    /// <summary>
    /// Deletes the instance's whole journal and its snapshot (retention purge, #862). Idempotent:
    /// deleting an instance with nothing stored is a no-op. Only for terminal instances — a later
    /// activation of the id starts from an empty journal, like an id that never existed.
    /// </summary>
    Task DeleteInstanceAsync(Guid instanceId);
}
