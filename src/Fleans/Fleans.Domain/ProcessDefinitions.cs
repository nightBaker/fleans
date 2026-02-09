using Orleans;

namespace Fleans.Domain;

/// <summary>
/// A deployed, immutable version of a workflow definition, similar to a Camunda process definition.
/// </summary>
[GenerateSerializer]
public sealed record ProcessDefinition
{
    /// <summary>
    /// Unique identifier for a deployed definition (e.g. "&lt;key&gt;:&lt;version&gt;:&lt;timestamp&gt;").
    /// </summary>
    [Id(0)]
    public required string ProcessDefinitionId { get; init; }

    /// <summary>
    /// BPMN process id (Camunda "process definition key").
    /// </summary>
    [Id(1)]
    public required string ProcessDefinitionKey { get; init; }

    /// <summary>
    /// Monotonically increasing version per key (1,2,3...).
    /// </summary>
    [Id(2)]
    public required int Version { get; init; }

    [Id(3)]
    public required DateTimeOffset DeployedAt { get; init; }

    [Id(4)]
    public required WorkflowDefinition Workflow { get; init; }

    [Id(5)]
    public required string BpmnXml { get; init; }
}

[GenerateSerializer]
public sealed record ProcessDefinitionSummary(
    [property: Id(0)] string ProcessDefinitionId,
    [property: Id(1)] string ProcessDefinitionKey,
    [property: Id(2)] int Version,
    [property: Id(3)] DateTimeOffset DeployedAt,
    [property: Id(4)] int ActivitiesCount,
    [property: Id(5)] int SequenceFlowsCount);

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

[GenerateSerializer]
public sealed record ActivityInstanceSnapshot(
    [property: Id(0)] Guid ActivityInstanceId,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] string ActivityType,
    [property: Id(3)] bool IsCompleted,
    [property: Id(4)] bool IsExecuting,
    [property: Id(5)] Guid VariablesStateId,
    [property: Id(6)] ActivityErrorState? ErrorState,
    [property: Id(7)] DateTimeOffset? CompletedAt);

[GenerateSerializer]
public sealed record VariableStateSnapshot(
    [property: Id(0)] Guid VariablesId,
    [property: Id(1)] Dictionary<string, string> Variables);

[GenerateSerializer]
public sealed record ConditionSequenceSnapshot(
    [property: Id(0)] string SequenceFlowId,
    [property: Id(1)] string Condition,
    [property: Id(2)] string SourceActivityId,
    [property: Id(3)] string TargetActivityId,
    [property: Id(4)] bool Result);

[GenerateSerializer]
public sealed record WorkflowInstanceInfo(
    [property: Id(0)] Guid InstanceId,
    [property: Id(1)] string ProcessDefinitionId,
    [property: Id(2)] bool IsStarted,
    [property: Id(3)] bool IsCompleted);
