using Orleans;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record BoundarableActivity(string ActivityId)
    : Activity(ActivityId)
{
    internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
        commands.AddRange(await BuildBoundaryRegistrationCommands(activityContext, definition));
        return commands;
    }

    private async Task<List<IExecutionCommand>> BuildBoundaryRegistrationCommands(
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = new List<IExecutionCommand>();

        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == ActivityId))
        {
            commands.Add(new RegisterTimerCommand(boundaryTimer.ActivityId,
                boundaryTimer.TimerDefinition.GetDueTime(), IsBoundary: true));
        }

        var variablesId = await activityContext.GetVariablesStateId();
        foreach (var boundaryMsg in definition.Activities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == ActivityId))
        {
            commands.Add(new RegisterMessageCommand(variablesId, boundaryMsg.MessageDefinitionId,
                boundaryMsg.ActivityId, IsBoundary: true));
        }

        foreach (var boundarySignal in definition.Activities.OfType<SignalBoundaryEvent>()
            .Where(bs => bs.AttachedToActivityId == ActivityId))
        {
            var signalDef = definition.Signals.First(s => s.Id == boundarySignal.SignalDefinitionId);
            commands.Add(new RegisterSignalCommand(signalDef.Name,
                boundarySignal.ActivityId, IsBoundary: true));
        }

        return commands;
    }
}
