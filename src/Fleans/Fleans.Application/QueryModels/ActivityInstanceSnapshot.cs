using Fleans.Domain;

namespace Fleans.Application.QueryModels;

public sealed record ActivityInstanceSnapshot(
    Guid ActivityInstanceId,
    string ActivityId,
    string ActivityType,
    bool IsCompleted,
    bool IsExecuting,
    Guid VariablesStateId,
    ActivityErrorState? ErrorState,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExecutionStartedAt,
    DateTimeOffset? CompletedAt,
    Guid? ChildWorkflowInstanceId = null);
