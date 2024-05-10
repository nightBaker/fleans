

namespace Fleans.Domain.Activities
{
    public abstract class Gateway : Activity
    {
        public override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityState state)
        {
            return new List<Activity>();
        }
    }



}
