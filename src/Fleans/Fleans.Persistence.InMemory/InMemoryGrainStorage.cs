using System.Collections.Concurrent;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.InMemory;

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
            _ => new StoredGrainState(grainState.State!, newETag),
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
        _store.TryRemove(key, out _);
        grainState.ETag = null;
        grainState.RecordExists = false;
        return Task.CompletedTask;
    }

    private static string MakeKey(string stateName, GrainId grainId)
        => $"{grainId}|{stateName}";

    private record StoredGrainState(object State, string ETag);
}
