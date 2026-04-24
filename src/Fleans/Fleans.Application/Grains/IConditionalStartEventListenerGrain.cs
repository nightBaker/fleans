using System.Dynamic;

namespace Fleans.Application.Grains;

public interface IConditionalStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask Register(string processDefinitionKey, string activityId, string conditionExpression);
    ValueTask Unregister();
    ValueTask<Guid?> EvaluateAndStart(ExpandoObject variables);
}
