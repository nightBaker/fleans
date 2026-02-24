

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

        // Only complete the workflow if this is a top-level EndEvent
        if (definition is not SubProcess)
            await workflowContext.Complete();
    }

    internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        return Task.FromResult(new List<Activity>());
    }
}
