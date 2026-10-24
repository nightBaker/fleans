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
    /// <summary>Per grain: each batch's id by its first version. Guarded by that grain's events-list lock.</summary>
    private readonly ConcurrentDictionary<Guid, Dictionary<int, Guid>> _batchIds = new();

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

    public Task<bool> AppendEventsAsync(Guid grainId, IReadOnlyList<WorkflowEvent> events, int expectedVersion, Guid batchId)
    {
        var list = _events.GetOrAdd(grainId, _ => []);
        var batchIds = _batchIds.GetOrAdd(grainId, _ => []);
        lock (list)
        {
            if (list.Count != expectedVersion)
                // A retry of a batch already stored at this version succeeds without appending.
                return Task.FromResult(batchIds.TryGetValue(expectedVersion + 1, out var stored) && stored == batchId);
            if (events.Count == 0)
                return Task.FromResult(true);
            batchIds[expectedVersion + 1] = batchId;
            list.AddRange(events);
        }
        return Task.FromResult(true);
    }

    public Task WriteSnapshotAsync(Guid grainId, int version, WorkflowInstanceState state)
    {
        _snapshots[grainId] = (copier.Copy(state), version);
        return Task.CompletedTask;
    }

    public Task DeleteInstanceAsync(Guid grainId)
    {
        _events.TryRemove(grainId, out _);
        _snapshots.TryRemove(grainId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Read-model projection for silos without a query store: records only the projected row.</summary>
internal class InMemoryWorkflowStateProjection : IWorkflowStateProjection
{
    private readonly ConcurrentDictionary<string, (WorkflowInstanceRow Row, int Version)> _rows = new();

    public Task<WorkflowInstanceRow?> ReadRowAsync(string grainId) =>
        Task.FromResult(_rows.TryGetValue(grainId, out var stored) ? stored.Row : null);

    public Task WriteAsync(string grainId, WorkflowInstanceState state, int version)
    {
        var row = WorkflowInstanceRow.From(state);
        _rows.AddOrUpdate(grainId, (row, version), (_, stored) => stored.Version < version ? (row, version) : stored);
        return Task.CompletedTask;
    }

    public async Task WriteBatchAsync(IReadOnlyList<ProjectionWrite> writes)
    {
        foreach (var write in writes)
            await WriteAsync(write.GrainId, write.State, write.Version);
    }
}

/// <summary>
/// Wraps the real projection, counts each grain's write attempts, and fails the next N batches
/// that carry a chosen grain (or the first that carries several chosen grains), to exercise the projector's handling of a read-model write that fails
/// after its append succeeded. A batch fails as a whole, as a real one does, so every other
/// instance in it fails too.
/// </summary>
internal class FaultInjectingWorkflowStateProjection(IWorkflowStateProjection inner) : IWorkflowStateProjection
{
    private static readonly ConcurrentDictionary<string, int> FailuresLeft = new();
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<WorkflowInstanceRow>> Writes = new();
    private static readonly ConcurrentQueue<IReadOnlyList<string>> Batches = new();
    private static readonly ConcurrentQueue<IReadOnlyList<string>> FailedBatches = new();

    private static HashSet<string>? _failBatchOfSeveral;

    public static void FailNextWrites(Guid instanceId, int count) => FailuresLeft[instanceId.ToString()] = count;

    /// <summary>Fails the first batch that carries at least two of <paramref name="instanceIds"/>.</summary>
    public static void FailFirstBatchOfSeveral(IEnumerable<Guid> instanceIds) =>
        Volatile.Write(ref _failBatchOfSeveral, instanceIds.Select(id => id.ToString()).ToHashSet());

    /// <summary>The row of every write attempt for the grain so far, in order.</summary>
    public static IReadOnlyList<WorkflowInstanceRow> WritesOf(Guid instanceId) =>
        Writes.TryGetValue(instanceId.ToString(), out var writes) ? writes.ToList() : [];

    /// <summary>The grain ids of every batch so far, failed ones included, in order.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllBatches => Batches.ToList();

    /// <summary>The grain ids of every injected-failure batch.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> FailedBatchMembers => FailedBatches.ToList();

    public Task<WorkflowInstanceRow?> ReadRowAsync(string grainId) => inner.ReadRowAsync(grainId);

    public Task WriteAsync(string grainId, WorkflowInstanceState state, int version) =>
        WriteBatchAsync([new ProjectionWrite(grainId, state, version)]);

    public Task WriteBatchAsync(IReadOnlyList<ProjectionWrite> writes)
    {
        var ids = writes.Select(w => w.GrainId).ToList();
        Batches.Enqueue(ids);
        foreach (var write in writes)
            Writes.GetOrAdd(write.GrainId, _ => new()).Enqueue(WorkflowInstanceRow.From(write.State));

        var failed = false;
        foreach (var id in ids.Distinct())
        {
            if (FailuresLeft.TryGetValue(id, out var left) && left > 0)
            {
                FailuresLeft[id] = left - 1;
                failed = true;
            }
        }
        if (Volatile.Read(ref _failBatchOfSeveral) is { } several
            && ids.Distinct().Count(several.Contains) >= 2
            && Interlocked.CompareExchange(ref _failBatchOfSeveral, null, several) == several)
        {
            failed = true;
        }

        if (failed)
        {
            FailedBatches.Enqueue(ids);
            throw new TimeoutException($"Injected failure of a projection batch of {ids.Count}");
        }
        return inner.WriteBatchAsync(writes);
    }
}


/// <summary>
/// Wraps the real event store so a test can hold or fail a chosen grain's next appends, to drive
/// the grain's journal writes into a chosen interleaving (#846) or through Orleans' re-read after a
/// failed append (#845), with or without the journal moving on meanwhile, or after storing the batch (#877). Other grains and later
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
    private static readonly ConcurrentDictionary<Guid, bool> StoreThenFail = new();

    /// <summary>Holds the grain's next <paramref name="count"/> appends, one gate each, in order.</summary>
    public static Gate[] HoldNextAppends(Guid instanceId, int count)
    {
        var gates = Enumerable.Range(0, count).Select(_ => new Gate()).ToArray();
        Gates[instanceId] = new ConcurrentQueue<Gate>(gates);
        return gates;
    }

    public static void FailNextAppends(Guid instanceId, int count) => FailuresLeft[instanceId] = count;

    /// <summary>
    /// Stores the grain's next append, then throws, as if the reply reporting the commit was lost (a
    /// timeout after the commit): Orleans retries a batch that is already stored (#877).
    /// </summary>
    public static void FailNextAppendAfterStoring(Guid instanceId) => StoreThenFail[instanceId] = true;

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

    public async Task<bool> AppendEventsAsync(Guid instanceId, IReadOnlyList<WorkflowEvent> events, int expectedVersion, Guid batchId)
    {
        if (Gates.TryGetValue(instanceId, out var gates) && gates.TryDequeue(out var gate))
        {
            gate.Entered.TrySetResult(events);
            await gate.Release.Task;
        }

        if (ForeignWrites.TryRemove(instanceId, out var foreign))
        {
            await inner.AppendEventsAsync(instanceId, [foreign], expectedVersion, Guid.NewGuid());
            throw new TimeoutException($"Injected append failure after a foreign write for {instanceId}");
        }

        if (StoreThenFail.TryRemove(instanceId, out _))
        {
            if (!await inner.AppendEventsAsync(instanceId, events, expectedVersion, batchId))
                throw new InvalidOperationException($"Store-then-fail append for {instanceId} conflicted");
            throw new TimeoutException($"Injected append failure after storing the batch for {instanceId}");
        }

        if (FailuresLeft.TryGetValue(instanceId, out var left) && left > 0)
        {
            FailuresLeft[instanceId] = left - 1;
            throw new TimeoutException($"Injected append failure for {instanceId}");
        }

        var stored = await inner.AppendEventsAsync(instanceId, events, expectedVersion, batchId);
        if (stored)
            Appended.GetOrAdd(instanceId, _ => new()).Enqueue(events.ToList());
        return stored;
    }

    public Task WriteSnapshotAsync(Guid instanceId, int version, WorkflowInstanceState state) =>
        inner.WriteSnapshotAsync(instanceId, version, state);

    public Task DeleteInstanceAsync(Guid instanceId) => inner.DeleteInstanceAsync(instanceId);
}
