using System.Dynamic;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface IConditionalStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask Register(string processDefinitionKey, string activityId, string conditionExpression);
    ValueTask Unregister();

    // [AlwaysInterleave] (#858): one listener per conditional start event, so a serialized evaluation
    // would queue every start of that event behind the one in flight. Safe because the call copies the
    // listener state before its first await and never writes it; Register/Unregister stay serialized.
    [AlwaysInterleave]
    ValueTask<Guid?> EvaluateAndStart(ExpandoObject variables);
}
