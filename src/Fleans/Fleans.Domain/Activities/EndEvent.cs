

using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record EndEvent(string ActivityId) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        await base.ExecuteAsync(workflowContext, activityContext);

        await activityContext.Complete();
        await workflowContext.Complete();
    }

    internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        return Task.FromResult(new List<Activity>());
    }
}
