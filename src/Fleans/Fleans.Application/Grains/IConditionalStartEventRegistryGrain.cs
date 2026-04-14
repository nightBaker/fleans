namespace Fleans.Application.Grains;

public interface IConditionalStartEventRegistryGrain : IGrainWithIntegerKey
{
    ValueTask Register(string processDefinitionKey, string activityId, string conditionExpression);
    ValueTask Unregister(string processDefinitionKey, string activityId);
    ValueTask UnregisterAllForProcess(string processDefinitionKey);
    ValueTask<List<ConditionalStartEntry>> GetAll();
    ValueTask<List<ConditionalStartEntry>> GetByProcess(string processDefinitionKey);
}
