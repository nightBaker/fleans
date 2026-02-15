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
        ValueTask<bool> IsCompleted();

        [ReadOnly]
        ValueTask<bool> IsExecuting();

        [ReadOnly]
        ValueTask<Guid> GetVariablesStateId();

        ValueTask Complete();
        ValueTask Fail(Exception exception);
        ValueTask Execute();
        ValueTask SetActivity(string activityId, string activityType);
        ValueTask SetVariablesId(Guid guid);
        Task PublishEvent(IDomainEvent domainEvent);
    }
}
