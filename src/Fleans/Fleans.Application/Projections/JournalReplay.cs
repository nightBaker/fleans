using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Application.Projections;

/// <summary>
/// Rebuilds an instance's state from its journal: the latest snapshot plus the events after it.
/// The grain loads itself this way on activation, and the read-model projector uses the same code,
/// so both see exactly the state the live run had.
/// </summary>
internal static class JournalReplay
{
    /// <returns>The state at the journal's head, that head's version, and the snapshot's version.</returns>
    public static async Task<(WorkflowInstanceState State, int Version, int SnapshotVersion)> LoadAsync(
        IEventStore eventStore, Guid instanceId)
    {
        // A failed read (database error, or a snapshot from an older build — pre-1.0 needs a fresh
        // database) propagates: the caller retries rather than degrading to a full replay.
        var (snapshot, snapshotVersion) = await eventStore.ReadSnapshotAsync(instanceId);
        var state = snapshot ?? new WorkflowInstanceState();
        var events = await eventStore.ReadEventsAsync(instanceId, snapshotVersion);
        if (events.Count > 0)
        {
            // Apply handlers read only the event (ids and OccurredAt included), so this rebuilds
            // exactly the state the live run had.
            var replay = new WorkflowExecution(state);
            foreach (var evt in events)
                replay.ReplayEvent(evt);
        }

        return (state, snapshotVersion + events.Count, snapshotVersion);
    }
}
