using Fleans.Domain.Activities;
using Fleans.Domain.Events;

namespace Fleans.Domain
{
    public interface IActivityInstance : IGrainWithGuidKey
    {
        ValueTask<Guid> GetActivityInstanceId();
        ValueTask<Activity> GetCurrentActivity();
        ValueTask<ActivityErrorState?> GetErrorState();
        ValueTask<bool> IsCompleted();
        ValueTask<bool> IsExecuting();
        ValueTask<Guid> GetVariablesStateId();
        void Complete();
        void Fail(Exception exception);
        void Execute();
        void SetActivity(Activity nextActivity);
        void SetVariablesId(Guid guid);
        Task PublishEvent(IDomainEvent domainEvent);
    }
}