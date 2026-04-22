using Orleans;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record BoundarableActivity(string ActivityId)
    : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        commands.AddRange(await BuildBoundaryRegistrationCommands(activityContext, definition));
        return commands;
    }

    private async Task<List<IExecutionCommand>> BuildBoundaryRegistrationCommands(
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = new List<IExecutionCommand>();

        foreach (var boundaryTimer in definition.GetBoundaryTimerEvents(ActivityId))
        {
            commands.Add(new RegisterTimerCommand(boundaryTimer.ActivityId,
                boundaryTimer.TimerDefinition.GetDueTime(), IsBoundary: true));
        }

        var variablesId = await activityContext.GetVariablesStateId();
        foreach (var boundaryMsg in definition.GetBoundaryMessageEvents(ActivityId))
        {
            commands.Add(new RegisterMessageCommand(variablesId, boundaryMsg.MessageDefinitionId,
                boundaryMsg.ActivityId, IsBoundary: true));
        }

        foreach (var boundarySignal in definition.GetBoundarySignalEvents(ActivityId))
        {
            var signalDef = definition.GetSignalDefinition(boundarySignal.SignalDefinitionId);
            commands.Add(new RegisterSignalCommand(signalDef.Name,
                boundarySignal.ActivityId, IsBoundary: true));
        }

        foreach (var boundaryMultiple in definition.GetBoundaryMultipleEvents(ActivityId))
        {
            foreach (var eventDef in boundaryMultiple.Definitions)
            {
                switch (eventDef)
                {
                    case TimerEventDef timerDef:
                        commands.Add(new RegisterTimerCommand(boundaryMultiple.ActivityId,
                            timerDef.TimerDefinition.GetDueTime(), IsBoundary: true));
                        break;
                    case MessageEventDef msgDef:
                        commands.Add(new RegisterMessageCommand(variablesId, msgDef.MessageDefinitionId,
                            boundaryMultiple.ActivityId, IsBoundary: true));
                        break;
                    case SignalEventDef sigDef:
                        var sigDefn = definition.GetSignalDefinition(sigDef.SignalDefinitionId);
                        commands.Add(new RegisterSignalCommand(sigDefn.Name,
                            boundaryMultiple.ActivityId, IsBoundary: true));
                        break;
                }
            }
        }

        return commands;
    }
}
