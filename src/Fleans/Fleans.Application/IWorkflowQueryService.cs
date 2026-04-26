using Fleans.Application.DTOs;
using Fleans.Application.QueryModels;
using Fleans.Domain.States;

namespace Fleans.Application;

public interface IWorkflowQueryService
{
    Task<InstanceStateSnapshot?> GetStateSnapshot(Guid workflowInstanceId);
    Task<IReadOnlyList<ProcessDefinitionSummary>> GetAllProcessDefinitions();
    Task<PagedResult<ProcessDefinitionSummary>> GetAllProcessDefinitions(PageRequest page);
    Task<PagedResult<ProcessDefinitionGroup>> GetProcessDefinitionGroups(PageRequest page);
    Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey, PageRequest page);
    Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string key, int version, PageRequest page);
    Task<string?> GetBpmnXml(Guid instanceId);
    Task<string?> GetBpmnXmlByKey(string processDefinitionKey);
    Task<string?> GetBpmnXmlByKeyAndVersion(string key, int version);
    Task<IReadOnlyList<UserTaskResponse>> GetPendingUserTasks(string? assignee = null, string? candidateGroup = null);
    Task<PagedResult<UserTaskResponse>> GetPendingUserTasks(string? assignee, string? candidateGroup, PageRequest page);
    Task<UserTaskResponse?> GetUserTask(Guid activityInstanceId);
    Task<IReadOnlyList<UserTaskState>> GetActiveUserTasksForWorkflow(Guid workflowInstanceId);
    Task<RegisteredEventsSnapshot> GetRegisteredEventsAsync();
}
