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
    Task<IWorkflowInstanceGrain> CreateInstance(string? businessId = null);
    Task<IWorkflowInstanceGrain> CreateInstanceByDefinitionId(string processDefinitionId, string? businessId = null);
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
