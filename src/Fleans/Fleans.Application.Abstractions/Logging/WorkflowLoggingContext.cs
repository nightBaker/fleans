using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Logging;

public static class WorkflowLoggingContext
{
    public static IDisposable? BeginWorkflowScope(
        ILogger logger, string workflowId, string? processDefinitionId,
        Guid workflowInstanceId, string? activityId = null, string? businessId = null)
    {
        RequestContext.Set(WorkflowContextKeys.WorkflowId, workflowId);
        RequestContext.Set(WorkflowContextKeys.WorkflowInstanceId, workflowInstanceId.ToString());
        if (processDefinitionId is not null)
            RequestContext.Set(WorkflowContextKeys.ProcessDefinitionId, processDefinitionId);
        if (activityId is not null)
            RequestContext.Set(WorkflowContextKeys.ActivityId, activityId);
        if (businessId is not null)
            RequestContext.Set(WorkflowContextKeys.BusinessId, businessId);
        else
            RequestContext.Remove(WorkflowContextKeys.BusinessId);

        return logger.BeginScope(
            "[{WorkflowId}, {ProcessDefinitionId}, {WorkflowInstanceId}, {BusinessId}, {ActivityId}]",
            workflowId, processDefinitionId ?? "-", workflowInstanceId, businessId ?? "-", activityId ?? "-");
    }
}
