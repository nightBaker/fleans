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

/// <summary>Read-model projection for silos without a query store: records only the projected version.</summary>
internal class InMemoryWorkflowStateProjection : IWorkflowStateProjection
{
    private readonly ConcurrentDictionary<string, int> _versions = new();

    public Task<int> ReadVersionAsync(string grainId) =>
        Task.FromResult(_versions.GetValueOrDefault(grainId));

    public Task WriteAsync(string grainId, WorkflowInstanceState state, int version)
    {
        _versions[grainId] = version;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Wraps the real projection and fails the next N writes for a chosen grain, to exercise the
/// grain's handling of a read-model write that fails after its append succeeded.
/// </summary>
internal class FaultInjectingWorkflowStateProjection(IWorkflowStateProjection inner) : IWorkflowStateProjection
{
    private static readonly ConcurrentDictionary<string, int> FailuresLeft = new();

    public static void FailNextWrites(Guid instanceId, int count) => FailuresLeft[instanceId.ToString()] = count;

    public Task<int> ReadVersionAsync(string grainId) => inner.ReadVersionAsync(grainId);

    public Task WriteAsync(string grainId, WorkflowInstanceState state, int version)
    {
        if (FailuresLeft.TryGetValue(grainId, out var left) && left > 0)
        {
            FailuresLeft[grainId] = left - 1;
            throw new TimeoutException($"Injected projection failure for {grainId}");
        }
        return inner.WriteAsync(grainId, state, version);
    }
}
