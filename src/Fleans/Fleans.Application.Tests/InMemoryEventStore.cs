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
    private readonly ConcurrentDictionary<string, List<IDomainEvent>> _events = new();
    private readonly ConcurrentDictionary<string, (WorkflowInstanceState State, int Version)> _snapshots = new();

    public Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(string grainId)
    {
        if (_snapshots.TryGetValue(grainId, out var snapshot))
            return Task.FromResult<(WorkflowInstanceState?, int)>((copier.Copy(snapshot.State), snapshot.Version));
        return Task.FromResult<(WorkflowInstanceState?, int)>((null, 0));
    }

    public Task<IReadOnlyList<IDomainEvent>> ReadEventsAsync(string grainId, int afterVersion)
    {
        if (!_events.TryGetValue(grainId, out var events))
            return Task.FromResult<IReadOnlyList<IDomainEvent>>([]);

        lock (events)
            return Task.FromResult<IReadOnlyList<IDomainEvent>>(events.Skip(afterVersion).ToList());
    }

    public Task<bool> AppendEventsAsync(string grainId, IReadOnlyList<IDomainEvent> events, int expectedVersion)
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

    public Task WriteSnapshotAsync(string grainId, int version, WorkflowInstanceState state)
    {
        _snapshots[grainId] = (copier.Copy(state), version);
        return Task.CompletedTask;
    }
}
