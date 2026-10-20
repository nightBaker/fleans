using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface IConditionalStartEventRegistryGrain : IGrainWithIntegerKey
{
    ValueTask Register(string processDefinitionKey, string activityId, string conditionExpression);
    ValueTask Unregister(string processDefinitionKey, string activityId);
    ValueTask UnregisterAllForProcess(string processDefinitionKey);
    // Orleans reads [ReadOnly] from the interface method, not the grain class.
    [ReadOnly]
    ValueTask<List<ConditionalStartEntry>> GetAll();
    [ReadOnly]
    ValueTask<List<ConditionalStartEntry>> GetByProcess(string processDefinitionKey);
}
