using Fleans.Domain.Activities;
using Fleans.Domain.Errors;

namespace Fleans.Domain.States;

// TODO: Add [GenerateSerializer] and [Id] attributes before implementing real storage.
public class ActivityInstanceState
{
    public Activity? CurrentActivity { get; private set; }
    public bool IsExecuting { get; private set; }
    public bool IsCompleted { get; private set; }
    public Guid VariablesId { get; private set; }
    public ActivityErrorState? ErrorState { get; private set; }
    public DateTimeOffset? CreatedAt { get; private set; }
    public DateTimeOffset? ExecutionStartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Complete()
    {
        IsExecuting = false;
        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(Exception exception)
    {
        if (exception is ActivityException activityException)
            ErrorState = activityException.GetActivityErrorState();
        else
            ErrorState = new ActivityErrorState(500, exception.Message);
    }

    public void Execute()
    {
        ErrorState = null;
        IsCompleted = false;
        IsExecuting = true;
        ExecutionStartedAt = DateTimeOffset.UtcNow;
    }

    public void SetActivity(Activity activity)
    {
        CurrentActivity = activity;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void SetVariablesId(Guid id) => VariablesId = id;

    public ActivityInstanceSnapshot GetSnapshot(Guid grainId) =>
        new(grainId, CurrentActivity!.ActivityId, CurrentActivity.GetType().Name,
            IsCompleted, IsExecuting, VariablesId, ErrorState,
            CreatedAt, ExecutionStartedAt, CompletedAt);
}
