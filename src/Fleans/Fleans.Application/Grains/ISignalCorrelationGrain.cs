namespace Fleans.Application.Grains;

public interface ISignalCorrelationGrain : IGrainWithStringKey
{
    ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId);
    ValueTask Unsubscribe(Guid workflowInstanceId, string activityId);
    ValueTask<int> BroadcastSignal();
}
