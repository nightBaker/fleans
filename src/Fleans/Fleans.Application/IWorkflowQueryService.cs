using Fleans.Application.DTOs;
using Fleans.Application.QueryModels;
using Fleans.Domain.States;

namespace Fleans.Application;

public interface IWorkflowQueryService
{
    Task<InstanceStateSnapshot?> GetStateSnapshot(Guid workflowInstanceId);
    Task<int> GetMaxVersionByKeyAsync(string processDefinitionKey);
    Task<PagedResult<ProcessDefinitionSummary>> GetAllProcessDefinitions(PageRequest page);
    Task<PagedResult<ProcessDefinitionGroup>> GetProcessDefinitionGroups(PageRequest page);
    /// <param name="businessId">Exact-match filter on the instance business id; unlike a Sieve
    /// <c>filters</c> term it may contain <c>,</c> and <c>|</c>.</param>
    Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey, PageRequest page, string? businessId = null);
    /// <inheritdoc cref="GetInstancesByKey"/>
    Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string key, int version, PageRequest page, string? businessId = null);
    Task<string?> GetBpmnXml(Guid instanceId);
    Task<string?> GetBpmnXmlByKey(string processDefinitionKey);
    Task<string?> GetBpmnXmlByKeyAndVersion(string key, int version);
    Task<IReadOnlyList<UserTaskResponse>> GetPendingUserTasks(string? assignee = null, string? candidateGroup = null);
    Task<PagedResult<UserTaskResponse>> GetPendingUserTasks(string? assignee, string? candidateGroup, PageRequest page);
    Task<UserTaskResponse?> GetUserTask(Guid activityInstanceId);
    Task<bool> UserTaskExists(Guid activityInstanceId);
    Task<IReadOnlyList<UserTaskState>> GetActiveUserTasksForWorkflow(Guid workflowInstanceId);
    Task<RegisteredEventsSnapshot> GetRegisteredEventsAsync(CancellationToken ct = default);

    /// <summary>Deployment requests (#900), newest first; all statuses when <paramref name="status"/> is null.</summary>
    Task<IReadOnlyList<DeploymentRequestSummary>> GetDeploymentRequests(DeploymentRequestStatus? status = null, int limit = 100);
    /// <summary>One deployment request with its BPMN XML, or <c>null</c>.</summary>
    Task<DeploymentRequestState?> GetDeploymentRequest(Guid requestId);
}
