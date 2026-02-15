using Fleans.Application.QueryModels;
using Fleans.Domain;
using System.Dynamic;

namespace Fleans.Application;

public interface IWorkflowCommandService
{
    Task<Guid> StartWorkflow(string workflowId);
    Task<Guid> StartWorkflowByProcessDefinitionId(string processDefinitionId);
    void CompleteActivity(Guid workflowInstanceId, string activityId, ExpandoObject variables);
    Task RegisterWorkflow(IWorkflowDefinition workflow);
    Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml);
}
