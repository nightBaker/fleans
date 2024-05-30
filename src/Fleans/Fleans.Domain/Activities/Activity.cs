
using Fleans.Domain.Events;

namespace Fleans.Domain.Activities
{
    public abstract class Activity
    {
        public string ActivityId { get; protected set; }
        internal virtual void Execute(WorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            activityInstance.Execute();
            workflowInstance.EnqueueEvent(new WorkflowActivityExecutedEvent(workflowInstance.WorkflowInstanceId,
                                                                            workflowInstance.Workflow.WorkflowId,
                                                                            activityInstance.ActivityInstanceId,
                                                                            ActivityId, 
                                                                            GetType().Name));
        }
        internal abstract List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance activityInstance);
    }
}
