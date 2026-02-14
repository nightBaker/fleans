using Orleans;

namespace Fleans.Domain;

/// <summary>
/// A deployed, immutable version of a workflow definition, similar to a Camunda process definition.
/// </summary>
[GenerateSerializer]
public sealed class ProcessDefinition
{
    /// <summary>
    /// Unique identifier for a deployed definition (e.g. "&lt;key&gt;:&lt;version&gt;:&lt;timestamp&gt;").
    /// </summary>
    [Id(0)]
    public required string ProcessDefinitionId { get; set; }

    /// <summary>
    /// BPMN process id (Camunda "process definition key").
    /// </summary>
    [Id(1)]
    public required string ProcessDefinitionKey { get; set; }

    /// <summary>
    /// Monotonically increasing version per key (1,2,3...).
    /// </summary>
    [Id(2)]
    public required int Version { get; set; }

    [Id(3)]
    public required DateTimeOffset DeployedAt { get; set; }

    [Id(4)]
    public required WorkflowDefinition Workflow { get; set; }

    [Id(5)]
    public required string BpmnXml { get; set; }
}
