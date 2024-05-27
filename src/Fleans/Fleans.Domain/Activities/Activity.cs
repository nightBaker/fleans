
using Fleans.Domain.Events;

namespace Fleans.Domain.Activities
{
    public abstract class Activity
    {
        public string ActivityId { get; protected set; }
        public virtual void Execute(WorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            activityInstance.Execute();
            workflowInstance.Events.Enqueue(new ActivityExecutedEvent(ActivityId, GetType().Name));
        }
        public abstract List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance activityInstance);
    }
}
