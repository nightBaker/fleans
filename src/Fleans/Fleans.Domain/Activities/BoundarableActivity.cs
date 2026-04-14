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

        foreach (var boundaryConditional in definition.GetBoundaryConditionalEvents(ActivityId))
        {
            commands.Add(new RegisterConditionalWatcherCommand(
                variablesId, boundaryConditional.ConditionExpression,
                boundaryConditional.ActivityId, IsBoundary: true));
        }

        return commands;
    }
}
