using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Logging;

internal static class WorkflowLoggingContext
{
    public static IDisposable? BeginWorkflowScope(
        ILogger logger, string workflowId, string? processDefinitionId,
        Guid workflowInstanceId, string? activityId = null)
    {
        RequestContext.Set("WorkflowId", workflowId);
        RequestContext.Set("WorkflowInstanceId", workflowInstanceId.ToString());
        if (processDefinitionId is not null)
            RequestContext.Set("ProcessDefinitionId", processDefinitionId);
        if (activityId is not null)
            RequestContext.Set("ActivityId", activityId);

        return logger.BeginScope(
            "[{WorkflowId}, {ProcessDefinitionId}, {WorkflowInstanceId}, {ActivityId}]",
            workflowId, processDefinitionId ?? "-", workflowInstanceId, activityId ?? "-");
    }
}
