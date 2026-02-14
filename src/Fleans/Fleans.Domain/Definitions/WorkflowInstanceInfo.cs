using Orleans;

namespace Fleans.Domain;

[GenerateSerializer]
public sealed record WorkflowInstanceInfo(
    [property: Id(0)] Guid InstanceId,
    [property: Id(1)] string ProcessDefinitionId,
    [property: Id(2)] bool IsStarted,
    [property: Id(3)] bool IsCompleted,
    [property: Id(4)] DateTimeOffset? CreatedAt,
    [property: Id(5)] DateTimeOffset? ExecutionStartedAt,
    [property: Id(6)] DateTimeOffset? CompletedAt);
