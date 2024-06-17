
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Orleans;

namespace Fleans.Domain
{
    public class ActivityInstance : Grain, IActivityInstance
    {

        private Activity _currentActivity;

        private bool _isCompleted;
        private bool _isExecuting;        
        private ActivityErrorState? _errorState;

        
        public void Complete()
        {
            _isExecuting = false;
            _isCompleted = true;
        }

        public void Fail(Exception exception)
        {
            if (exception is ActivityException activityException)
            {
                _errorState = activityException.GetActivityErrorState();
            }
            else
            {
                _errorState = new ActivityErrorState(500, exception.Message);
            }

            Complete();
        }

        public void Execute()
        {
            _errorState = null;
            _isCompleted = false;
            _isExecuting = true;
        }

        public ValueTask<Guid> GetActivityInstanceId() => ValueTask.FromResult( this.GetPrimaryKey());

        public ValueTask<Activity> GetCurrentActivity() => ValueTask.FromResult(_currentActivity);

        public ValueTask<ActivityErrorState?> GetErrorState() => ValueTask.FromResult(_errorState);

        ValueTask<bool> IActivityInstance.IsCompleted() => ValueTask.FromResult(_isCompleted);

        ValueTask<bool> IActivityInstance.IsExecuting() => ValueTask.FromResult(_isExecuting);

        public ValueTask<Guid> GetVariablesStateId() => ValueTask.FromResult(this.GetPrimaryKey());

        public void SetActivity(Activity nextActivity)
        {
            _currentActivity = nextActivity;
        }
    }

    [GenerateSerializer]
    public record ActivityErrorState(int Code, string Message);


}
