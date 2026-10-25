namespace Fleans.Application.Grains;

/// <summary>
/// The persisted row of one conditional start event, read by the Events page. Evaluation does not
/// pass through this grain: <c>StartEventLauncher</c> works from the registry, cached per host (#858).
/// </summary>
public interface IConditionalStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask Register(string processDefinitionKey, string activityId, string conditionExpression);
    ValueTask Unregister();
}
