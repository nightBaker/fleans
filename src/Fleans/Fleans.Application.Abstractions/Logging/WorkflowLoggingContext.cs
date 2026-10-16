using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Logging;

/// <summary>
/// Single place that writes the workflow correlation ids into <see cref="RequestContext"/> and
/// opens the matching log scope. <see cref="WorkflowLoggingScopeFilter"/> reads the same keys back
/// on the receiving grain.
/// </summary>
public static class WorkflowLoggingContext
{
    /// <summary>
    /// Points <see cref="RequestContext"/> at one workflow instance. The business id belongs to
    /// that instance, so it is removed when the instance has none — otherwise a caller's id (e.g.
    /// a call-activity parent's) would leak into the new instance's downstream calls.
    /// </summary>
    public static void SetInstance(
        string workflowId, string? processDefinitionId, Guid workflowInstanceId, string? businessId)
    {
        RequestContext.Set(WorkflowContextKeys.WorkflowId, workflowId);
        RequestContext.Set(WorkflowContextKeys.WorkflowInstanceId, workflowInstanceId.ToString());
        if (processDefinitionId is not null)
            RequestContext.Set(WorkflowContextKeys.ProcessDefinitionId, processDefinitionId);
        if (businessId is not null)
            RequestContext.Set(WorkflowContextKeys.BusinessId, businessId);
        else
            RequestContext.Remove(WorkflowContextKeys.BusinessId);
    }

    public static IDisposable? BeginScope(
        ILogger logger, string workflowId, string? processDefinitionId,
        Guid workflowInstanceId, string? businessId, string? activityId = null)
        => logger.BeginScope(
            "[{WorkflowId}, {ProcessDefinitionId}, {WorkflowInstanceId}, {BusinessId}, {ActivityId}]",
            workflowId, processDefinitionId ?? "-", workflowInstanceId.ToString(), businessId ?? "-", activityId ?? "-");

    /// <summary>
    /// For stream handlers. Events that do not carry the business id pass <c>null</c>; the value
    /// that arrived with the stream's <see cref="RequestContext"/> is then kept.
    /// </summary>
    public static IDisposable? BeginWorkflowScope(
        ILogger logger, string workflowId, string? processDefinitionId,
        Guid workflowInstanceId, string? activityId = null, string? businessId = null)
    {
        businessId ??= RequestContext.Get(WorkflowContextKeys.BusinessId) as string;
        SetInstance(workflowId, processDefinitionId, workflowInstanceId, businessId);
        if (activityId is not null)
            RequestContext.Set(WorkflowContextKeys.ActivityId, activityId);

        return BeginScope(logger, workflowId, processDefinitionId, workflowInstanceId, businessId, activityId);
    }
}
