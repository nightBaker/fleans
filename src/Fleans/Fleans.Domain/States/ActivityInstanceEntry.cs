using Fleans.Domain.Errors;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class ActivityInstanceEntry
{
    public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId, Guid? scopeId = null)
    {
        ActivityInstanceId = activityInstanceId;
        ActivityId = activityId;
        WorkflowInstanceId = workflowInstanceId;
        ScopeId = scopeId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private ActivityInstanceEntry()
    {
    }

    [Id(0)]
    public Guid ActivityInstanceId { get; private set; }

    [Id(1)]
    public string ActivityId { get; private set; } = null!;

    [Id(2)]
    public Guid WorkflowInstanceId { get; private set; }

    [Id(3)]
    public bool IsCompleted { get; private set; }

    [Id(4)]
    public Guid? ChildWorkflowInstanceId { get; private set; }

    [Id(5)]
    public Guid? ScopeId { get; private set; }

    [Id(6)]
    public int? MultiInstanceIndex { get; private set; }

    // Execution state fields
    [Id(7)]
    public string? ActivityType { get; private set; }

    [Id(8)]
    public bool IsExecuting { get; private set; }

    [Id(9)]
    public bool IsCancelled { get; private set; }

    [Id(10)]
    public string? CancellationReason { get; private set; }

    [Id(11)]
    public Guid VariablesId { get; private set; }

    [Id(12)]
    public string? ErrorCode { get; private set; }

    [Id(13)]
    public string? ErrorMessage { get; private set; }

    [Id(14)]
    public Guid? TokenId { get; private set; }

    [Id(15)]
    public int? MultiInstanceTotal { get; private set; }

    // Timestamps
    [Id(16)]
    public DateTimeOffset? CreatedAt { get; private set; }

    [Id(17)]
    public DateTimeOffset? ExecutionStartedAt { get; private set; }

    [Id(18)]
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// True when this entry represents a compensation handler spawned during a compensation walk.
    /// Compensation handlers are excluded from the compensation snapshot hook (no re-compensation)
    /// and tracked separately from normal scope children.
    /// </summary>
    [Id(19)]
    public bool IsCompensationHandler { get; private set; }

    // Computed
    public ActivityErrorState? ErrorState =>
        ErrorCode is not null ? new ActivityErrorState(ErrorCode, ErrorMessage!) : null;

    public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId, Guid? scopeId, int multiInstanceIndex)
        : this(activityInstanceId, activityId, workflowInstanceId, scopeId)
    {
        MultiInstanceIndex = multiInstanceIndex;
    }

    public void SetChildWorkflowInstanceId(Guid childId) => ChildWorkflowInstanceId = childId;

    // State transition methods
    public void Execute()
    {
        if (IsExecuting)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already executing.");
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed — cannot execute again.");

        IsExecuting = true;
        ExecutionStartedAt = DateTimeOffset.UtcNow;
    }

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
            ErrorCode = "500";
            ErrorMessage = exception.Message;
        }

        IsExecuting = false;
        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(string errorCode, string errorMessage)
    {
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed — cannot fail.");

        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
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

    public void ResetExecuting()
    {
        if (!IsExecuting)
            return;

        IsExecuting = false;
    }

    // Setters for initialization
    public void SetActivityType(string activityType)
    {
        ActivityType = activityType;
    }

    public void SetVariablesId(Guid id) => VariablesId = id;

    public void SetMultiInstanceIndex(int index) => MultiInstanceIndex = index;

    public void SetMultiInstanceTotal(int total) => MultiInstanceTotal = total;

    public void SetTokenId(Guid id) => TokenId = id;

    internal void MarkAsCompensationHandler() => IsCompensationHandler = true;
}
