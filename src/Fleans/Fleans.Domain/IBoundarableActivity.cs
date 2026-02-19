namespace Fleans.Domain;

public interface IBoundarableActivity
{
    Task RegisterBoundaryEventsAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext);
}
