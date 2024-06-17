

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record EndEvent : Activity
{
    public EndEvent(string activityId) : base(activityId)
    { 
        ActivityId = activityId;
    }

    internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityState)
    {
        await base.ExecuteAsync(workflowInstance, activityState);

        activityState.Complete();
        workflowInstance.Complete();            
    }

    internal override Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, IActivityInstance activityState)
    {
        return Task.FromResult(new List<Activity>());
    }
}
