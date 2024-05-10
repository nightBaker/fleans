

namespace Fleans.Domain.Activities
{
    public class EndEvent : Activity
    {
        public override void Execute(WorkflowInstance workflowInstance, ActivityState activityState)
        {
            activityState.Complete();
            workflowInstance.Complete();
        }

        public override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityState activityState)
        {
            return new List<Activity>();
        }
    }
}
