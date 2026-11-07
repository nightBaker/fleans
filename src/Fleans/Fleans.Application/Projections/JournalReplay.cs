using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Application.Projections;

/// <summary>
/// Rebuilds an instance's state from its journal: the latest snapshot plus the events after it.
/// The grain loads itself this way on activation, and the read-model projector uses the same code,
/// so both see the state the live run had. A snapshot lacks the finished history pruned before it
/// was written (#1050); <see cref="LoadFullHistoryAsync"/> rebuilds that too.
/// </summary>
internal static class JournalReplay
{
    /// <param name="eventStore">The journal.</param>
    /// <param name="instanceId">The instance to rebuild.</param>
    /// <param name="knownVersion">
    /// A version the journal is known to have reached (the projector's notification), or 0 when
    /// unknown. Below <see cref="IEventStore.SnapshotInterval"/> the instance most likely has no
    /// snapshot yet (#903), so the journal is replayed from the start in one read instead of a
    /// snapshot read plus a tail read. That is correct whatever the head is — the journal holds
    /// every event — and if the head has meanwhile run past what one read may load, it falls back
    /// to the snapshot.
    /// </param>
    /// <returns>The state at the journal's head, that head's version, and the snapshot's version.</returns>
    public static async Task<(WorkflowInstanceState State, int Version, int SnapshotVersion)> LoadAsync(
        IEventStore eventStore, Guid instanceId, int knownVersion = 0)
    {
        if (knownVersion > 0 && knownVersion < eventStore.SnapshotInterval)
        {
            IReadOnlyList<WorkflowEvent>? all = null;
            try
            {
                all = await eventStore.ReadEventsAsync(instanceId, 0);
            }
            catch (InvalidOperationException)
            {
                // More events than one read may load: only the snapshot path can rebuild it.
            }

            if (all is not null)
                return (Replay(new WorkflowInstanceState(), all), all.Count, 0);
        }

        // A failed read (database error, or a snapshot from an older build — pre-1.0 needs a fresh
        // database) propagates: the caller retries rather than degrading to a full replay.
        var (snapshot, snapshotVersion) = await eventStore.ReadSnapshotAsync(instanceId);
        var events = await eventStore.ReadEventsAsync(instanceId, snapshotVersion);
        return (Replay(snapshot ?? new WorkflowInstanceState(), events), snapshotVersion + events.Count, snapshotVersion);
    }

    /// <summary>
    /// Rebuilds the state with its whole history by replaying the journal from the start, one page
    /// at a time so that a long journal is not all in memory. Replay does not prune.
    /// </summary>
    /// <returns>The state at the journal's head and that head's version.</returns>
    public static async Task<(WorkflowInstanceState State, int Version)> LoadFullHistoryAsync(
        IEventStore eventStore, Guid instanceId)
    {
        var state = new WorkflowInstanceState();
        var version = 0;
        while (true)
        {
            var page = await eventStore.ReadEventPageAsync(instanceId, version);
            if (page.Count == 0)
                return (state, version);
            Replay(state, page);
            version += page.Count;
        }
    }

    /// <summary>The instance's whole journal, read page by page.</summary>
    public static async Task<List<WorkflowEvent>> ReadWholeJournalAsync(IEventStore eventStore, Guid instanceId)
    {
        var events = new List<WorkflowEvent>();
        while (true)
        {
            var page = await eventStore.ReadEventPageAsync(instanceId, events.Count);
            if (page.Count == 0)
                return events;
            events.AddRange(page);
        }
    }

    private static WorkflowInstanceState Replay(WorkflowInstanceState state, IReadOnlyList<WorkflowEvent> events)
    {
        if (events.Count > 0)
        {
            // Apply handlers read only the event (ids and OccurredAt included), so this rebuilds
            // exactly the state the live run had.
            var replay = new WorkflowExecution(state);
            foreach (var evt in events)
                replay.ReplayEvent(evt);
        }

        return state;
    }
}
