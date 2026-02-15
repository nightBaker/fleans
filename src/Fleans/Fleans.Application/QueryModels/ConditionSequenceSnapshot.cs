namespace Fleans.Application.QueryModels;

public sealed record ConditionSequenceSnapshot(
    string SequenceFlowId,
    string Condition,
    string SourceActivityId,
    string TargetActivityId,
    bool Result);
