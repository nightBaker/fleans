using Orleans;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record BoundarableActivity(string ActivityId)
    : Activity(ActivityId), IBoundarableActivity
{
    public async Task RegisterBoundaryEventsAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var hostInstanceId = await activityContext.GetActivityInstanceId();

        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterTimerReminder(hostInstanceId, boundaryTimer.ActivityId, boundaryTimer.TimerDefinition.GetDueTime());
        }

        var variablesId = await activityContext.GetVariablesStateId();
        foreach (var boundaryMsg in definition.Activities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterBoundaryMessageSubscription(variablesId, hostInstanceId, boundaryMsg.ActivityId, boundaryMsg.MessageDefinitionId);
        }

        foreach (var boundarySignal in definition.Activities.OfType<SignalBoundaryEvent>()
            .Where(bs => bs.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterBoundarySignalSubscription(hostInstanceId, boundarySignal.ActivityId, boundarySignal.SignalDefinitionId);
        }
    }
}
