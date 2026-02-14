using Orleans;

namespace Fleans.Domain;

[GenerateSerializer]
public sealed record InstanceStateSnapshot(
    [property: Id(0)] List<string> ActiveActivityIds,
    [property: Id(1)] List<string> CompletedActivityIds,
    [property: Id(2)] bool IsStarted,
    [property: Id(3)] bool IsCompleted,
    [property: Id(4)] List<ActivityInstanceSnapshot> ActiveActivities,
    [property: Id(5)] List<ActivityInstanceSnapshot> CompletedActivities,
    [property: Id(6)] List<VariableStateSnapshot> VariableStates,
    [property: Id(7)] List<ConditionSequenceSnapshot> ConditionSequences);
