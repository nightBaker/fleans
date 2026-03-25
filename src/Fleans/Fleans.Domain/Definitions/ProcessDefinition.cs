using System.Text.RegularExpressions;
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

    /// <summary>
    /// Concurrency token managed by the persistence layer (e.g. EF Core).
    /// Uses <c>set</c> instead of <c>init</c> because the store updates it after each write.
    /// </summary>
    [Id(6)]
    public string? ETag { get; set; }

    [Id(7)]
    public bool IsActive { get; private set; } = true;

    public void Disable()
    {
        IsActive = false;
    }

    public void Enable()
    {
        IsActive = true;
    }

    /// <summary>
    /// Copies relevant mutable state from a previous version of this process definition.
    /// Currently preserves the disabled state so that redeploying a disabled process
    /// does not silently re-enable it.
    /// </summary>
    /// <summary>
    /// Extracts the process definition key from a ProcessDefinitionId string.
    /// Format: {key}:{version}:{timestamp}[-{collision}]
    /// The key can contain colons, so we match the suffix pattern from the right.
    /// </summary>
    public static string ExtractKeyFromId(string processDefinitionId)
    {
        var match = Regex.Match(processDefinitionId, @":\d+:\d{8}T\d{6}\.\d{7}Z(-\d+)?$");
        if (!match.Success)
            throw new ArgumentException($"Invalid ProcessDefinitionId format: {processDefinitionId}", nameof(processDefinitionId));
        return processDefinitionId[..match.Index];
    }

    public void InheritStateFrom(ProcessDefinition previousVersion)
    {
        if (!previousVersion.IsActive)
        {
            Disable();
        }
    }
}
