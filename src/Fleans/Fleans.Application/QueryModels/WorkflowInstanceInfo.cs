namespace Fleans.Application.QueryModels;

/// <summary>
/// One row of an instance listing, read from the read model. <see cref="StateVersion"/> is the
/// journal version the row was projected to (#861); the live instance may be ahead of it.
/// </summary>
public sealed record WorkflowInstanceInfo(
    Guid InstanceId,
    string ProcessDefinitionId,
    bool IsStarted,
    bool IsCompleted,
    bool IsCancelled,
    bool IsFailed,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExecutionStartedAt,
    DateTimeOffset? CompletedAt,
    string? BusinessId,
    int StateVersion);
