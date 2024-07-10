using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Orleans.Concurrency;

namespace Fleans.Domain
{
    public interface IActivityInstance : IGrainWithGuidKey
    {
        [ReadOnly]
        ValueTask<Guid> GetActivityInstanceId();

        [ReadOnly]
        ValueTask<Activity> GetCurrentActivity();

        [ReadOnly]
        ValueTask<ActivityErrorState?> GetErrorState();

        [ReadOnly]
        ValueTask<bool> IsCompleted();

        [ReadOnly]
        ValueTask<bool> IsExecuting();

        [ReadOnly]
        ValueTask<Guid> GetVariablesStateId();

        void Complete();
        void Fail(Exception exception);
        void Execute();
        void SetActivity(Activity nextActivity);
        void SetVariablesId(Guid guid);
        Task PublishEvent(IDomainEvent domainEvent);
    }
}