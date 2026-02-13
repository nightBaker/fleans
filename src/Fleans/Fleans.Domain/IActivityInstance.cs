using Fleans.Domain.Events;
using Orleans.Concurrency;

namespace Fleans.Domain
{
    public interface IActivityInstance : IGrainWithGuidKey
    {
        [ReadOnly]
        ValueTask<Guid> GetActivityInstanceId();

        [ReadOnly]
        ValueTask<string> GetActivityId();

        [ReadOnly]
        ValueTask<ActivityErrorState?> GetErrorState();

        [ReadOnly]
        ValueTask<DateTimeOffset?> GetCreatedAt();

        [ReadOnly]
        ValueTask<DateTimeOffset?> GetExecutionStartedAt();

        [ReadOnly]
        ValueTask<DateTimeOffset?> GetCompletedAt();

        [ReadOnly]
        ValueTask<bool> IsCompleted();

        [ReadOnly]
        ValueTask<bool> IsExecuting();

        [ReadOnly]
        ValueTask<Guid> GetVariablesStateId();

        [ReadOnly]
        ValueTask<ActivityInstanceSnapshot> GetSnapshot();

        ValueTask Complete();
        ValueTask Fail(Exception exception);
        ValueTask Execute();
        ValueTask SetActivity(string activityId, string activityType);
        ValueTask SetVariablesId(Guid guid);
        Task PublishEvent(IDomainEvent domainEvent);
    }
}