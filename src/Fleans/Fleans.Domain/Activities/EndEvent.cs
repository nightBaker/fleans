

using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record EndEvent(string ActivityId) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        await base.ExecuteAsync(workflowContext, activityContext, definition);

        await activityContext.Complete();

        // Only mark the whole workflow as complete when we are at the root process level.
        // Inside a sub-process scope, scope completion is detected by WorkflowScopeState
        // tracking (O(1)) and the sub-process activity completes automatically.
        if (definition.IsRootProcess)
            await workflowContext.Complete();
    }

    internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        return Task.FromResult(new List<Activity>());
    }
}
