using Fleans.Application.QueryModels;
using Fleans.Domain;

namespace Fleans.Application.WorkflowFactory;

public interface IWorkflowInstanceFactoryGrain : IGrainWithIntegerKey
{
    Task<IWorkflowInstance> CreateWorkflowInstanceGrain(string workflowId);
    Task<IWorkflowInstance> CreateWorkflowInstanceGrainByProcessDefinitionId(string processDefinitionId);
    Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml);
    Task RegisterWorkflow(IWorkflowDefinition workflow);
    Task<bool> IsWorkflowRegistered(string workflowId);
}
