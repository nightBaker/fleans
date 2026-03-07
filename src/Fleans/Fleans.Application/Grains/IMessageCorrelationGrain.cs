using System.Dynamic;

namespace Fleans.Application.Grains;

public interface IMessageCorrelationGrain : IGrainWithStringKey
{
    ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId);
    ValueTask Unsubscribe();
    ValueTask<bool> DeliverMessage(ExpandoObject variables);
}
