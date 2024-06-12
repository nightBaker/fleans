

namespace Fleans.Domain.Activities
{
    public class EndEvent : Activity
    {
        public EndEvent(string activityId) 
        { 
            ActivityId = activityId;
        }

        internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            await base.ExecuteAsync(workflowInstance, activityState);

            activityState.Complete();
            workflowInstance.Complete();            
        }

        internal override Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            return Task.FromResult(new List<Activity>());
        }
    }
}
