using Fleans.Application.QueryModels;
using Fleans.Domain;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

/// <summary>
/// Per-process-key grain that manages all versions of a process definition.
/// Keyed by process definition key (BPMN process id).
/// Replaces both the old singleton WorkflowInstanceFactoryGrain and
/// the old per-version ProcessDefinitionGrain.
/// </summary>
public interface IProcessDefinitionGrain : IGrainWithStringKey
{
    Task<ProcessDefinitionSummary> DeployVersion(WorkflowDefinition workflow, string bpmnXml);

    [ReadOnly]
    Task<IWorkflowDefinition> GetLatestDefinition();

    [ReadOnly]
    Task<WorkflowDefinition> GetDefinitionById(string processDefinitionId);

    /// <summary>
    /// The definition a new instance starts from: the latest version, or the version
    /// <paramref name="processDefinitionId"/> names. Returns <c>null</c> when the process is
    /// disabled. This is the one process-disabled check, and every start path calls it before
    /// <see cref="IWorkflowInstanceGrain.StartWorkflow"/> (#863): a disabled process starts no new
    /// instance, whether the start comes from the API, a message, signal, conditional or timer
    /// start event, or a call activity. Throws <see cref="KeyNotFoundException"/> when nothing is
    /// deployed under this key or the version is unknown.
    /// </summary>
    [ReadOnly]
    Task<IWorkflowDefinition?> GetStartableDefinition(string? processDefinitionId = null);

    [ReadOnly]
    Task<bool> IsActive();

    Task<ProcessDefinitionSummary> Disable();
    Task<ProcessDefinitionSummary> Enable();
}
