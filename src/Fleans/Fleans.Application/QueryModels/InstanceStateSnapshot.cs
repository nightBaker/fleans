namespace Fleans.Application.QueryModels;

/// <summary>
/// The state of one instance. <see cref="StateVersion"/> is the journal version the state reflects:
/// the grain's own version when <see cref="IsFromReadModel"/> is false (the exact, live state), or
/// the version the read model was projected to when it is true (#861).
/// </summary>
[GenerateSerializer]
public sealed record InstanceStateSnapshot(
    [property: Id(0)] List<string> ActiveActivityIds,
    [property: Id(1)] List<string> CompletedActivityIds,
    [property: Id(2)] bool IsStarted,
    [property: Id(3)] bool IsCompleted,
    [property: Id(4)] bool IsCancelled,
    [property: Id(5)] bool IsFailed,
    [property: Id(6)] List<ActivityInstanceSnapshot> ActiveActivities,
    [property: Id(7)] List<ActivityInstanceSnapshot> CompletedActivities,
    [property: Id(8)] List<VariableStateSnapshot> VariableStates,
    [property: Id(9)] List<ConditionSequenceSnapshot> ConditionSequences,
    [property: Id(10)] string? ProcessDefinitionId,
    [property: Id(11)] DateTimeOffset? CreatedAt,
    [property: Id(12)] DateTimeOffset? ExecutionStartedAt,
    [property: Id(13)] DateTimeOffset? CompletedAt,
    [property: Id(14)] string? BusinessId,
    [property: Id(15)] int StateVersion,
    [property: Id(16)] bool IsFromReadModel);
