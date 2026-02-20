using Fleans.Application.QueryModels;
using Fleans.Domain;
using Orleans.Concurrency;

namespace Fleans.Application.WorkflowFactory;

public interface IWorkflowInstanceFactoryGrain : IGrainWithIntegerKey
{
    Task<Guid> CreateWorkflowInstanceGrain(string workflowId);
    Task<Guid> CreateWorkflowInstanceGrainByProcessDefinitionId(string processDefinitionId);
    Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml);

    [ReadOnly]
    Task<IWorkflowDefinition> GetLatestWorkflowDefinition(string processDefinitionKey);
}
