using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Orleans.Concurrency;

namespace Fleans.Application.WorkflowFactory;

public interface IWorkflowInstanceFactoryGrain : IGrainWithIntegerKey
{
    Task<IWorkflowInstanceGrain> CreateWorkflowInstanceGrain(string workflowId);
    Task<IWorkflowInstanceGrain> CreateWorkflowInstanceGrainByProcessDefinitionId(string processDefinitionId);
    Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml);

    [ReadOnly]
    Task<IWorkflowDefinition> GetLatestWorkflowDefinition(string processDefinitionKey);

    Task<ProcessDefinitionSummary> DisableProcess(string processDefinitionKey);
    Task<ProcessDefinitionSummary> EnableProcess(string processDefinitionKey);
}
