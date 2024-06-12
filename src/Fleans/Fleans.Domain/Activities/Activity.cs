
using Fleans.Domain.Events;
using Orleans;

namespace Fleans.Domain.Activities
{
    public abstract class Activity
    {
        public string ActivityId { get; protected set; }

        internal virtual async Task ExecuteAsync(IWorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            var defintion = await workflowInstance.GetWorkflowDefinition();
            activityInstance.Execute();
            workflowInstance.EnqueueEvent(new WorkflowActivityExecutedEvent(await workflowInstance.GetWorkflowInstanceId(),
                                                                            defintion.WorkflowId,
                                                                            activityInstance.ActivityInstanceId,
                                                                            ActivityId, 
                                                                            GetType().Name));            
        }
        internal abstract Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, ActivityInstance activityInstance);
    }
}
