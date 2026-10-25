using Orleans.Concurrency;

namespace Fleans.Application.Grains;

/// <summary>
/// The persisted list of every conditional start event. Read by every host's
/// <c>StartEventSubscriptionCache</c> on a miss, never per evaluation (#858).
/// </summary>
public interface IConditionalStartEventRegistryGrain : IGrainWithIntegerKey
{
    ValueTask Register(string processDefinitionKey, string activityId, string conditionExpression);
    ValueTask Unregister(string processDefinitionKey, string activityId);
    ValueTask UnregisterAllForProcess(string processDefinitionKey);
    // Orleans reads [ReadOnly] from the interface method, not the grain class.
    [ReadOnly]
    ValueTask<List<ConditionalStartEntry>> GetAll();
}
