using Orleans;

namespace Fleans.Domain;

[GenerateSerializer]
public sealed record ConditionSequenceSnapshot(
    [property: Id(0)] string SequenceFlowId,
    [property: Id(1)] string Condition,
    [property: Id(2)] string SourceActivityId,
    [property: Id(3)] string TargetActivityId,
    [property: Id(4)] bool Result);
