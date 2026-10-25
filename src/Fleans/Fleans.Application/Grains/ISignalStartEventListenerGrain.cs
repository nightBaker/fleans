using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface ISignalStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask RegisterProcess(string processDefinitionKey);
    ValueTask UnregisterProcess(string processDefinitionKey);

    // Read by StartEventSubscriptionCache on a miss; starts do not call this grain (#858).
    [ReadOnly]
    ValueTask<List<string>> GetProcessKeys();
}
