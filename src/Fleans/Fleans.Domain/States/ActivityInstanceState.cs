using Fleans.Domain.Errors;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class ActivityInstanceState
{
    [Id(0)]
    public Guid Id { get; private set; }

    [Id(1)]
    public string? ETag { get; private set; }

    [Id(2)]
    public string? ActivityId { get; private set; }

    [Id(3)]
    public string? ActivityType { get; private set; }

    [Id(4)]
    public bool IsExecuting { get; private set; }

    [Id(5)]
    public bool IsCompleted { get; private set; }

    [Id(6)]
    public Guid VariablesId { get; private set; }

    [Id(7)]
    public int? ErrorCode { get; private set; }

    [Id(8)]
    public string? ErrorMessage { get; private set; }

    [Id(9)]
    public DateTimeOffset? CreatedAt { get; private set; }

    [Id(10)]
    public DateTimeOffset? ExecutionStartedAt { get; private set; }

    [Id(11)]
    public DateTimeOffset? CompletedAt { get; private set; }

    public ActivityErrorState? ErrorState =>
        ErrorCode is not null ? new ActivityErrorState(ErrorCode.Value, ErrorMessage!) : null;

    public void Complete()
    {
        IsExecuting = false;
        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(Exception exception)
    {
        if (exception is ActivityException activityException)
        {
            var errorState = activityException.GetActivityErrorState();
            ErrorCode = errorState.Code;
            ErrorMessage = errorState.Message;
        }
        else
        {
            ErrorCode = 500;
            ErrorMessage = exception.Message;
        }
    }

    public void Execute()
    {
        ErrorCode = null;
        ErrorMessage = null;
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
