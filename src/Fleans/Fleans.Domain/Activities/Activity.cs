
namespace Fleans.Domain.Activities
{
    public abstract class Activity
    {
        public string ActivityId { get; protected set; }
        public abstract void Execute(WorkflowInstance workflowInstance, ActivityInstance activityInstance);
        public abstract List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance activityInstance);
    }
}
