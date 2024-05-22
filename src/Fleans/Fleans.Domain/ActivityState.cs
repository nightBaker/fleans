
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;

namespace Fleans.Domain
{
    public class ActivityInstance
    {
        public Activity CurrentActivity { get; }

        public bool IsCompleted { get; private set; }

        public bool IsExecuting { get; private set; }

        public Guid VariablesStateId { get; internal set; }

        public ActivityErrorState? ErrorState { get; private set; }

        public ActivityInstance(Activity currentActivity)
        {
            CurrentActivity = currentActivity;
        }

        internal void Complete()
        {
            IsExecuting = false;
            IsCompleted = true;
        }

        internal void Fail(Exception exception)
        {
            if (exception is ActivityException activityException)
            {
                ErrorState = activityException.GetActivityErrorState();
            }
            else
            {
                ErrorState = new ActivityErrorState(500, exception.Message);
            }

            Complete();
        }

        internal void Execute()
        {
            ErrorState = null;
            IsCompleted = false;
            IsExecuting = true;
        }
    }

    public record ActivityErrorState(int Code, string Message);


}
