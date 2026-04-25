using Fleans.Application.QueryModels;
using Fleans.Domain;
using System.Dynamic;

namespace Fleans.Application;

public interface IWorkflowCommandService
{
    Task<Guid> StartWorkflow(string workflowId, ExpandoObject? initialVariables = null);
    Task<Guid> StartWorkflowByProcessDefinitionId(string processDefinitionId);
    Task CompleteActivity(Guid workflowInstanceId, string activityId, ExpandoObject variables);
    Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml);
    Task<SendMessageResult> SendMessage(string messageName, string? correlationKey, ExpandoObject variables);
    Task<SendSignalResult> SendSignal(string signalName);

    // User task lifecycle
    Task ClaimUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId);
    Task UnclaimUserTask(Guid workflowInstanceId, Guid activityInstanceId);
    Task CompleteUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId, ExpandoObject variables);

    Task<ProcessDefinitionSummary> DisableProcess(string processDefinitionKey);
    Task<ProcessDefinitionSummary> EnableProcess(string processDefinitionKey);
    Task<EvaluateConditionsResult> EvaluateConditions(string? workflowId, ExpandoObject variables);
}

public record EvaluateConditionsResult(List<Guid> StartedInstanceIds, List<string>? Errors = null);

public record SendMessageResult(bool Delivered, List<Guid>? WorkflowInstanceIds = null);
public record SendSignalResult(int DeliveredCount, List<Guid>? WorkflowInstanceIds = null, List<string>? Errors = null);
