using Fleans.Application.QueryModels;
using Fleans.Domain;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

/// <summary>
/// Per-process-key grain that manages all versions of a process definition.
/// Replaces the singleton WorkflowInstanceFactoryGrain for per-key operations.
/// </summary>
public interface IProcessDefinitionGrain : IGrainWithStringKey
{
    Task<IWorkflowInstanceGrain> CreateInstance();
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
