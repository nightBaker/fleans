using Fleans.Domain;

namespace Fleans.Application.QueryModels;

[GenerateSerializer]
public sealed record ActivityInstanceSnapshot(
    [property: Id(0)] Guid ActivityInstanceId,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] string ActivityType,
    [property: Id(3)] bool IsCompleted,
    [property: Id(4)] bool IsExecuting,
    [property: Id(5)] bool IsCancelled,
    [property: Id(6)] Guid VariablesStateId,
    [property: Id(7)] ActivityErrorState? ErrorState,
    [property: Id(8)] string? CancellationReason,
    [property: Id(9)] DateTimeOffset? CreatedAt,
    [property: Id(10)] DateTimeOffset? ExecutionStartedAt,
    [property: Id(11)] DateTimeOffset? CompletedAt,
    [property: Id(12)] Guid? ChildWorkflowInstanceId = null);
