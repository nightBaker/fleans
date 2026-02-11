using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Infrastructure.Storage;

public class ActivityInstanceGrainStorage : IGrainStorage
{
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        return Task.CompletedTask;
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        return Task.CompletedTask;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        return Task.CompletedTask;
    }
}
