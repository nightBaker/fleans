namespace Fleans.Application.QueryModels;

public sealed record WorkflowInstanceInfo(
    Guid InstanceId,
    string ProcessDefinitionId,
    bool IsStarted,
    bool IsCompleted,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExecutionStartedAt,
    DateTimeOffset? CompletedAt);
