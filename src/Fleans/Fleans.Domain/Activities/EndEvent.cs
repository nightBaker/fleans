

namespace Fleans.Domain.Activities
{
    public class EndEvent : Activity
    {
        public EndEvent(string activityId) 
        { 
            ActivityId = activityId;
        }

        internal override void Execute(WorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            base.Execute(workflowInstance, activityState);

            activityState.Complete();
            workflowInstance.Complete();            
        }

        internal override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            return new List<Activity>();
        }
    }
}
