using System.Collections.Concurrent;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.InMemory;

// Note: State is stored by reference. Mutations to the grain's state object
// are visible in the store without WriteStateAsync. This matches Orleans'
// built-in MemoryGrainStorage behavior.
public class InMemoryGrainStorage : IGrainStorage
{
    private readonly ConcurrentDictionary<string, StoredGrainState> _store = new();

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var key = MakeKey(stateName, grainId);
        if (_store.TryGetValue(key, out var stored))
        {
            grainState.State = (T)stored.State;
            grainState.ETag = stored.ETag;
            grainState.RecordExists = true;
        }

        return Task.CompletedTask;
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var key = MakeKey(stateName, grainId);
        var newETag = Guid.NewGuid().ToString("N");

        _store.AddOrUpdate(key,
            _ =>
            {
                if (grainState.ETag is not null)
                    throw new InconsistentStateException(
                        $"ETag mismatch: expected '{grainState.ETag}', but no record exists");
                return new StoredGrainState(grainState.State!, newETag);
            },
            (_, existing) =>
            {
                if (existing.ETag != grainState.ETag)
                    throw new InconsistentStateException(
                        $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");
                return new StoredGrainState(grainState.State!, newETag);
            });

        grainState.ETag = newETag;
        grainState.RecordExists = true;
        return Task.CompletedTask;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var key = MakeKey(stateName, grainId);
        if (_store.TryGetValue(key, out var existing) && existing.ETag != grainState.ETag)
            throw new InconsistentStateException(
                $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");
        _store.TryRemove(key, out _);
        grainState.ETag = null;
        grainState.RecordExists = false;
        return Task.CompletedTask;
    }

    private static string MakeKey(string stateName, GrainId grainId)
        => $"{grainId}|{stateName}";

    private record StoredGrainState(object State, string ETag);
}
