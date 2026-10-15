using System.Dynamic;
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
    Task<IWorkflowInstanceGrain> CreateInstance();
    /// <summary>
    /// Creates and starts an instance of the latest version with optional initial variables and
    /// business id (#824). Throws <see cref="InvalidOperationException"/> (HTTP 409) when business
    /// id uniqueness rejects the start.
    /// </summary>
    Task<Guid> StartNewInstance(ExpandoObject? initialVariables, string? businessId);
    Task<IWorkflowInstanceGrain> CreateInstanceByDefinitionId(string processDefinitionId);
    Task<ProcessDefinitionSummary> DeployVersion(WorkflowDefinition workflow, string bpmnXml);

    [ReadOnly]
    Task<IWorkflowDefinition> GetLatestDefinition();

    [ReadOnly]
    Task<WorkflowDefinition> GetDefinitionById(string processDefinitionId);

    [ReadOnly]
    Task<bool> IsActive();

    Task<ProcessDefinitionSummary> Disable();
    Task<ProcessDefinitionSummary> Enable();
}
