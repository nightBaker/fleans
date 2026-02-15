namespace Fleans.Application.QueryModels;

public sealed record InstanceStateSnapshot(
    List<string> ActiveActivityIds,
    List<string> CompletedActivityIds,
    bool IsStarted,
    bool IsCompleted,
    List<ActivityInstanceSnapshot> ActiveActivities,
    List<ActivityInstanceSnapshot> CompletedActivities,
    List<VariableStateSnapshot> VariableStates,
    List<ConditionSequenceSnapshot> ConditionSequences,
    string? ProcessDefinitionId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExecutionStartedAt,
    DateTimeOffset? CompletedAt);
