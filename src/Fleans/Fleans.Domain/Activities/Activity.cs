
using System.Runtime.CompilerServices;
using Fleans.Domain.Events;
using Orleans;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Activity([property: Id(0)] string ActivityId)
{
    internal virtual async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        await activityContext.Execute();
        await activityContext.PublishEvent(new WorkflowActivityExecutedEvent(await workflowContext.GetWorkflowInstanceId(),
            definition.WorkflowId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            GetType().Name));

        // Register boundary events attached to this activity
        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterTimerReminder(boundaryTimer.ActivityId, boundaryTimer.TimerDefinition.GetDueTime());
        }

        foreach (var boundaryMsg in definition.Activities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterBoundaryMessageSubscription(boundaryMsg.ActivityId, boundaryMsg.MessageDefinitionId);
        }
    }
    internal abstract Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext);
}
