using Fleans.Domain;

namespace Fleans.Application.WorkflowFactory;

public interface IWorkflowInstanceFactoryGrain : IGrainWithIntegerKey
{
    Task<IWorkflowInstance> CreateWorkflowInstanceGrain(string workflowId);
    Task<IWorkflowInstance> CreateWorkflowInstanceGrainByProcessDefinitionId(string processDefinitionId);
    Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml);
    Task RegisterWorkflow(IWorkflowDefinition workflow);
    Task<bool> IsWorkflowRegistered(string workflowId);
    Task<IReadOnlyList<IWorkflowDefinition>> GetAllWorkflows();
    Task<IReadOnlyList<ProcessDefinitionSummary>> GetAllProcessDefinitions();
    Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey);
    Task<string> GetBpmnXml(string processDefinitionId);
    Task<string> GetBpmnXmlByInstanceId(Guid instanceId);
}

public record WorkflowSummary(string WorkflowId, int ActivitiesCount, int SequenceFlowsCount);
