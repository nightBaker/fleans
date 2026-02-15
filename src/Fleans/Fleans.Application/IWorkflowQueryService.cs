using Fleans.Application.QueryModels;

namespace Fleans.Application;

public interface IWorkflowQueryService
{
    Task<InstanceStateSnapshot?> GetStateSnapshot(Guid workflowInstanceId);
    Task<IReadOnlyList<ProcessDefinitionSummary>> GetAllProcessDefinitions();
    Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey);
    Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string key, int version);
    Task<string?> GetBpmnXml(Guid instanceId);
    Task<string?> GetBpmnXmlByKey(string processDefinitionKey);
    Task<string?> GetBpmnXmlByKeyAndVersion(string key, int version);
}
