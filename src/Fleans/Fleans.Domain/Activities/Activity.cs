
using Fleans.Domain.Events;
using Orleans;

namespace Fleans.Domain.Activities
{
    [GenerateSerializer]
    public abstract record Activity(string ActivityId)
    {
        internal virtual async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
        {
            var defintion = await workflowInstance.GetWorkflowDefinition();
            activityInstance.Execute();
            await activityInstance.PublishEvent(new WorkflowActivityExecutedEvent(await workflowInstance.GetWorkflowInstanceId(),
                                                                            defintion.WorkflowId,
                                                                            await activityInstance.GetActivityInstanceId(),
                                                                            ActivityId,
                                                                            GetType().Name));
        }
        internal abstract Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, IActivityInstance activityInstance);
    }
}
