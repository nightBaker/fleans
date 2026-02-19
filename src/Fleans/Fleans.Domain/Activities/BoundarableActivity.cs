using Orleans;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record BoundarableActivity(string ActivityId)
    : Activity(ActivityId), IBoundarableActivity
{
    public async Task RegisterBoundaryEventsAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var hostInstanceId = await activityContext.GetActivityInstanceId();

        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterTimerReminder(hostInstanceId, boundaryTimer.ActivityId, boundaryTimer.TimerDefinition.GetDueTime());
        }

        foreach (var boundaryMsg in definition.Activities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterBoundaryMessageSubscription(hostInstanceId, boundaryMsg.ActivityId, boundaryMsg.MessageDefinitionId);
        }
    }
}
