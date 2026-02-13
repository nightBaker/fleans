using Fleans.Domain.Errors;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class ActivityInstanceState
{
    [Id(0)]
    public string? ActivityId { get; internal set; }

    [Id(1)]
    public string? ActivityType { get; internal set; }

    [Id(2)]
    public bool IsExecuting { get; internal set; }

    [Id(3)]
    public bool IsCompleted { get; internal set; }

    [Id(4)]
    public Guid VariablesId { get; internal set; }

    [Id(5)]
    public ActivityErrorState? ErrorState { get; internal set; }

    [Id(6)]
    public DateTimeOffset? CreatedAt { get; internal set; }

    [Id(7)]
    public DateTimeOffset? ExecutionStartedAt { get; internal set; }

    [Id(8)]
    public DateTimeOffset? CompletedAt { get; internal set; }

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

    public void SetActivity(string activityId, string activityType)
    {
        ActivityId = activityId;
        ActivityType = activityType;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void SetVariablesId(Guid id) => VariablesId = id;

    public ActivityInstanceSnapshot GetSnapshot(Guid grainId) =>
        new(grainId, ActivityId!, ActivityType!,
            IsCompleted, IsExecuting, VariablesId, ErrorState,
            CreatedAt, ExecutionStartedAt, CompletedAt);
}
