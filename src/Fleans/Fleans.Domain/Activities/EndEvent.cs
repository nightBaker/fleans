

namespace Fleans.Domain.Activities
{
    public class EndEvent : Activity
    {
        public EndEvent(string activityId) 
        { 
            ActivityId = activityId;
        }

        public override void Execute(WorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            base.Execute(workflowInstance, activityState);

            activityState.Complete();
            workflowInstance.Complete();            
        }

        public override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            return new List<Activity>();
        }
    }
}
