using Orleans;

namespace Fleans.Domain;

[GenerateSerializer]
public sealed record ActivityInstanceSnapshot(
    [property: Id(0)] Guid ActivityInstanceId,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] string ActivityType,
    [property: Id(3)] bool IsCompleted,
    [property: Id(4)] bool IsExecuting,
    [property: Id(5)] Guid VariablesStateId,
    [property: Id(6)] ActivityErrorState? ErrorState,
    [property: Id(7)] DateTimeOffset? CreatedAt,
    [property: Id(8)] DateTimeOffset? ExecutionStartedAt,
    [property: Id(9)] DateTimeOffset? CompletedAt);
