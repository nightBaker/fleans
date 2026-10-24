using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.States;
using System.Dynamic;

namespace Fleans.Application;

public interface IWorkflowCommandService
{
    Task<Guid> StartWorkflow(string workflowId, ExpandoObject? initialVariables = null, string? businessId = null);
    Task<Guid> StartWorkflowByProcessDefinitionId(string processDefinitionId, string? businessId = null);
    Task CompleteActivity(Guid workflowInstanceId, string activityId, ExpandoObject variables);
    /// <summary>
    /// Deploys immediately. Throws <see cref="Deployments.DeploymentApprovalRequiredException"/> when
    /// <c>Fleans:Deployments:RequiredApprovals</c> is above 0 — use <see cref="SubmitDeployment"/> then.
    /// </summary>
    Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml);
    /// <summary>
    /// Delivers a message to the running instance subscribed to <paramref name="correlationKey"/>, or
    /// else starts every process with a matching message start event. With an
    /// <paramref name="idempotencyKey"/>, a repeat of the send (same message name and key, within
    /// <c>MessageDeduplicationState.Window</c>) does nothing and returns the first send's outcome (#743).
    /// </summary>
    Task<SendMessageResult> SendMessage(string messageName, string? correlationKey, ExpandoObject variables,
        string? businessId = null, string? idempotencyKey = null);
    Task<SendSignalResult> SendSignal(string signalName);

    // User task lifecycle
    Task ClaimUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId, IReadOnlyList<string> userGroups);
    Task UnclaimUserTask(Guid workflowInstanceId, Guid activityInstanceId);
    Task CompleteUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId, ExpandoObject variables);
    Task FailUserTask(Guid workflowInstanceId, Guid activityInstanceId, string errorCode, string errorMessage);
    Task CancelUserTask(Guid workflowInstanceId, Guid activityInstanceId, string? reason);

    Task<ProcessDefinitionSummary> DisableProcess(string processDefinitionKey);
    Task<ProcessDefinitionSummary> EnableProcess(string processDefinitionKey);
    Task<EvaluateConditionsResult> EvaluateConditions(string? workflowId, ExpandoObject variables);

    // Deployment requests (#900): the request needs Fleans:Deployments:RequiredApprovals approvals
    // from distinct non-requesters, then deploys itself. With 0 required it deploys on submit.
    Task<DeploymentRequestState> SubmitDeployment(string bpmnXml, string requestedBy, string? comment, bool deployDisabled);
    // userGroups: the reviewer's groups, checked against Fleans:Deployments:ReviewerGroups.
    Task<DeploymentRequestState> ApproveDeployment(Guid requestId, string userId, IReadOnlyList<string> userGroups, string? comment);
    Task<DeploymentRequestState> RejectDeployment(Guid requestId, string userId, IReadOnlyList<string> userGroups, string reason);
    Task<DeploymentRequestState> WithdrawDeployment(Guid requestId, string userId);
    Task<DeploymentRequestState> RetryDeployment(Guid requestId);
}

public record EvaluateConditionsResult(List<Guid> StartedInstanceIds, List<string>? Errors = null);

[GenerateSerializer]
public record SendMessageResult(
    [property: Id(0)] bool Delivered,
    [property: Id(1)] List<Guid>? WorkflowInstanceIds = null);
public record SendSignalResult(int DeliveredCount, List<Guid>? WorkflowInstanceIds = null, List<string>? Errors = null);
