
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Orleans;

namespace Fleans.Domain
{
    public class ActivityInstance : Grain, IActivityInstance
    {

        private Activity _currentActivity;

        private bool _isCompleted;
        private bool _isExecuting;        
        private ActivityErrorState? _errorState;
        private Guid _variablesId;

        
        public ValueTask Complete()
        {
            _isExecuting = false;
            _isCompleted = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask Fail(Exception exception)
        {
            if (exception is ActivityException activityException)
            {
                _errorState = activityException.GetActivityErrorState();
            }
            else
            {
                _errorState = new ActivityErrorState(500, exception.Message);
            }

            return Complete();
        }

        public ValueTask Execute()
        {
            _errorState = null;
            _isCompleted = false;
            _isExecuting = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<Guid> GetActivityInstanceId() => ValueTask.FromResult( this.GetPrimaryKey());

        public ValueTask<Activity> GetCurrentActivity() => ValueTask.FromResult(_currentActivity);

        public ValueTask<ActivityErrorState?> GetErrorState() => ValueTask.FromResult(_errorState);

        ValueTask<bool> IActivityInstance.IsCompleted() 
            => ValueTask.FromResult(_isCompleted);

        ValueTask<bool> IActivityInstance.IsExecuting() => ValueTask.FromResult(_isExecuting);

        public ValueTask<Guid> GetVariablesStateId() 
            => ValueTask.FromResult(_variablesId);

        public ValueTask SetVariablesId(Guid guid)
        {
            _variablesId = guid;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetActivity(Activity nextActivity)
        {
            _currentActivity = nextActivity;
            return ValueTask.CompletedTask;
        }

        public Task PublishEvent(IDomainEvent domainEvent)
        {
            var eventPublisher = GrainFactory.GetGrain<IEventPublisher>(0);
            return eventPublisher.Publish(domainEvent);
        }
    }

    [GenerateSerializer]
    public record ActivityErrorState(int Code, string Message);


}
