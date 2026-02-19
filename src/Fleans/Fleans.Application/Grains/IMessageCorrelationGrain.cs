using System.Dynamic;

namespace Fleans.Application.Grains;

public interface IMessageCorrelationGrain : IGrainWithStringKey
{
    ValueTask Subscribe(string correlationKey, Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId);
    ValueTask Unsubscribe(string correlationKey);
    ValueTask<bool> DeliverMessage(string correlationKey, ExpandoObject variables);
}
