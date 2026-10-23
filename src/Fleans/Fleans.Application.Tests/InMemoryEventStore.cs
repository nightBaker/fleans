using Fleans.Domain.Events;
using Fleans.Domain.States;
using Orleans.Serialization;
using System.Collections.Concurrent;

namespace Fleans.Application.Tests;

/// <summary>
/// In-memory IEventStore for lightweight tests that don't need EF Core persistence.
/// Snapshots are deep copies, like a real store: a later mutation of the live grain state must
/// not leak into a stored snapshot.
/// </summary>
internal class InMemoryEventStore(DeepCopier copier) : IEventStore
{
    private readonly ConcurrentDictionary<Guid, List<WorkflowEvent>> _events = new();
    private readonly ConcurrentDictionary<Guid, (WorkflowInstanceState State, int Version)> _snapshots = new();

    public int SnapshotInterval => 1;

    public Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(Guid grainId)
    {
        if (_snapshots.TryGetValue(grainId, out var snapshot))
            return Task.FromResult<(WorkflowInstanceState?, int)>((copier.Copy(snapshot.State), snapshot.Version));
        return Task.FromResult<(WorkflowInstanceState?, int)>((null, 0));
    }

    public Task<IReadOnlyList<WorkflowEvent>> ReadEventsAsync(Guid grainId, int afterVersion)
    {
        if (!_events.TryGetValue(grainId, out var events))
            return Task.FromResult<IReadOnlyList<WorkflowEvent>>([]);

        lock (events)
            return Task.FromResult<IReadOnlyList<WorkflowEvent>>(events.Skip(afterVersion).ToList());
    }

    public Task<bool> AppendEventsAsync(Guid grainId, IReadOnlyList<WorkflowEvent> events, int expectedVersion)
    {
        var list = _events.GetOrAdd(grainId, _ => []);
        lock (list)
        {
            if (list.Count != expectedVersion)
                return Task.FromResult(false);
            list.AddRange(events);
        }
        return Task.FromResult(true);
    }

    public Task WriteSnapshotAsync(Guid grainId, int version, WorkflowInstanceState state)
    {
        _snapshots[grainId] = (copier.Copy(state), version);
        return Task.CompletedTask;
    }
}

/// <summary>Read-model projection for silos without a query store: records only the projected row.</summary>
internal class InMemoryWorkflowStateProjection : IWorkflowStateProjection
{
    private readonly ConcurrentDictionary<string, WorkflowInstanceRow> _rows = new();

    public Task<WorkflowInstanceRow?> ReadRowAsync(string grainId) =>
        Task.FromResult(_rows.GetValueOrDefault(grainId));

    public Task WriteAsync(string grainId, WorkflowInstanceState state)
    {
        _rows[grainId] = WorkflowInstanceRow.From(state);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Wraps the real projection, counts each grain's write attempts, and fails the next N writes for
/// a chosen grain, to exercise the grain's handling of a read-model write that fails after its
/// append succeeded.
/// </summary>
internal class FaultInjectingWorkflowStateProjection(IWorkflowStateProjection inner) : IWorkflowStateProjection
{
    private static readonly ConcurrentDictionary<string, int> FailuresLeft = new();
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<WorkflowInstanceRow>> Writes = new();

    public static void FailNextWrites(Guid instanceId, int count) => FailuresLeft[instanceId.ToString()] = count;

    /// <summary>The row of every write attempt for the grain so far, in order.</summary>
    public static IReadOnlyList<WorkflowInstanceRow> WritesOf(Guid instanceId) =>
        Writes.TryGetValue(instanceId.ToString(), out var writes) ? writes.ToList() : [];

    public Task<WorkflowInstanceRow?> ReadRowAsync(string grainId) => inner.ReadRowAsync(grainId);

    public Task WriteAsync(string grainId, WorkflowInstanceState state)
    {
        Writes.GetOrAdd(grainId, _ => new()).Enqueue(WorkflowInstanceRow.From(state));
        if (FailuresLeft.TryGetValue(grainId, out var left) && left > 0)
        {
            FailuresLeft[grainId] = left - 1;
            throw new TimeoutException($"Injected projection failure for {grainId}");
        }
        return inner.WriteAsync(grainId, state);
    }
}

/// <summary>
/// Wraps the real event store so a test can hold or fail a chosen grain's next appends, to drive
/// the grain's journal writes into a chosen interleaving (#846) or through Orleans' re-read after a
/// failed append (#845), with or without the journal moving on meanwhile. Other grains and later
/// appends pass straight through.
/// </summary>
internal class FaultInjectingEventStore(IEventStore inner) : IEventStore
{
    /// <summary>
    /// One held append: <see cref="Entered"/> completes with the batch when it reaches the store, and
    /// the append waits for <see cref="Release"/>.
    /// </summary>
    public sealed class Gate
    {
        public TaskCompletionSource<IReadOnlyList<WorkflowEvent>> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<Gate>> Gates = new();
    private static readonly ConcurrentDictionary<Guid, int> FailuresLeft = new();
    private static readonly ConcurrentDictionary<Guid, WorkflowEvent> ForeignWrites = new();
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<IReadOnlyList<WorkflowEvent>>> Appended = new();

    /// <summary>Every batch the grain appended so far, in order: one entry per stored append.</summary>
    public static IReadOnlyList<IReadOnlyList<WorkflowEvent>> AppendsOf(Guid instanceId) =>
        Appended.TryGetValue(instanceId, out var appends) ? appends.ToList() : [];

    /// <summary>Holds the grain's next <paramref name="count"/> appends, one gate each, in order.</summary>
    public static Gate[] HoldNextAppends(Guid instanceId, int count)
    {
        var gates = Enumerable.Range(0, count).Select(_ => new Gate()).ToArray();
        Gates[instanceId] = new ConcurrentQueue<Gate>(gates);
        return gates;
    }

    public static void FailNextAppends(Guid instanceId, int count) => FailuresLeft[instanceId] = count;

    /// <summary>
    /// Fails the grain's next append after storing <paramref name="foreign"/> in its place, as if
    /// another writer got there first: the journal moves on without the grain.
    /// </summary>
    public static void FailNextAppendAfterForeignWrite(Guid instanceId, WorkflowEvent foreign) =>
        ForeignWrites[instanceId] = foreign;

    public int SnapshotInterval => inner.SnapshotInterval;

    public Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(Guid instanceId) =>
        inner.ReadSnapshotAsync(instanceId);

    public Task<IReadOnlyList<WorkflowEvent>> ReadEventsAsync(Guid instanceId, int afterVersion) =>
        inner.ReadEventsAsync(instanceId, afterVersion);

    public async Task<bool> AppendEventsAsync(Guid instanceId, IReadOnlyList<WorkflowEvent> events, int expectedVersion)
    {
        if (Gates.TryGetValue(instanceId, out var gates) && gates.TryDequeue(out var gate))
        {
            gate.Entered.TrySetResult(events);
            await gate.Release.Task;
        }

        if (ForeignWrites.TryRemove(instanceId, out var foreign))
        {
            await inner.AppendEventsAsync(instanceId, [foreign], expectedVersion);
            throw new TimeoutException($"Injected append failure after a foreign write for {instanceId}");
        }

        if (FailuresLeft.TryGetValue(instanceId, out var left) && left > 0)
        {
            FailuresLeft[instanceId] = left - 1;
            throw new TimeoutException($"Injected append failure for {instanceId}");
        }

        var stored = await inner.AppendEventsAsync(instanceId, events, expectedVersion);
        if (stored)
            Appended.GetOrAdd(instanceId, _ => new()).Enqueue(events.ToList());
        return stored;
    }

    public Task WriteSnapshotAsync(Guid instanceId, int version, WorkflowInstanceState state) =>
        inner.WriteSnapshotAsync(instanceId, version, state);
}
