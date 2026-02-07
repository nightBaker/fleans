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
    [property: Id(3)] bool IsCompleted);

[GenerateSerializer]
public sealed record WorkflowInstanceInfo(
    [property: Id(0)] Guid InstanceId,
    [property: Id(1)] string ProcessDefinitionId,
    [property: Id(2)] bool IsStarted,
    [property: Id(3)] bool IsCompleted);
