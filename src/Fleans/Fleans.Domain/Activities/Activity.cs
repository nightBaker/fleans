
namespace Fleans.Domain.Activities
{
    public abstract class Activity
    {
        public Guid ActivityId { get; protected set; }
        public abstract void Execute(WorkflowInstance workflowInstance, ActivityState activityState);
        public abstract List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityState activityState);
    }
}
