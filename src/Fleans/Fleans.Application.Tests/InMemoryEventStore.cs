using Fleans.Domain.Events;
using Fleans.Domain.States;
using System.Collections.Concurrent;

namespace Fleans.Application.Tests;

/// <summary>
/// In-memory IEventStore for lightweight tests that don't need EF Core persistence.
/// </summary>
internal class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, List<IDomainEvent>> _events = new();
    private readonly ConcurrentDictionary<string, (WorkflowInstanceState State, int Version)> _snapshots = new();

    public Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(string grainId)
    {
        if (_snapshots.TryGetValue(grainId, out var snapshot))
            return Task.FromResult<(WorkflowInstanceState?, int)>((snapshot.State, snapshot.Version));
        return Task.FromResult<(WorkflowInstanceState?, int)>((null, 0));
    }

    public Task<IReadOnlyList<IDomainEvent>> ReadEventsAsync(string grainId, int afterVersion)
    {
        if (!_events.TryGetValue(grainId, out var events))
            return Task.FromResult<IReadOnlyList<IDomainEvent>>([]);

        return Task.FromResult<IReadOnlyList<IDomainEvent>>(
            events.Skip(afterVersion).ToList());
    }

    public Task<bool> AppendEventsAsync(string grainId, IReadOnlyList<IDomainEvent> events, int startVersion)
    {
        var list = _events.GetOrAdd(grainId, _ => []);
        lock (list)
        {
            if (list.Count != startVersion)
                return Task.FromResult(false);
            list.AddRange(events);
        }
        return Task.FromResult(true);
    }

    public Task WriteSnapshotAsync(string grainId, int version, WorkflowInstanceState state)
    {
        _snapshots[grainId] = (state, version);
        return Task.CompletedTask;
    }

    public Task ProjectStateAsync(string grainId, WorkflowInstanceState state) =>
        Task.CompletedTask;
}
