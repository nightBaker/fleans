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

    [Id(12)]
    public bool IsCancelled { get; private set; }

    [Id(13)]
    public string? CancellationReason { get; private set; }

    public ActivityErrorState? ErrorState =>
        ErrorCode is not null ? new ActivityErrorState(ErrorCode.Value, ErrorMessage!) : null;

    public void Complete()
    {
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed.");

        IsExecuting = false;
        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(Exception exception)
    {
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed — cannot fail.");

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

        IsExecuting = false;
        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Cancel(string reason)
    {
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed — cannot cancel.");

        IsCancelled = true;
        CancellationReason = reason;
        IsExecuting = false;
        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Execute()
    {
        if (IsExecuting)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already executing.");
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed — cannot execute again.");

        IsExecuting = true;
        ExecutionStartedAt = DateTimeOffset.UtcNow;
    }

    public void ResetExecuting()
    {
        if (!IsExecuting)
            return;

        IsExecuting = false;
    }

    public void SetActivity(string activityId, string activityType)
    {
        ActivityId = activityId;
        ActivityType = activityType;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void SetVariablesId(Guid id) => VariablesId = id;
}
