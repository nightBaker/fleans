using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record MultipleIntermediateCatchEvent(
    string ActivityId,
    [property: Id(1)] List<EventDefinition> Definitions) : BoundarableActivity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        var variablesId = await activityContext.GetVariablesStateId();

        foreach (var def in Definitions)
        {
            switch (def)
            {
                case MessageEventDef msgDef:
                    commands.Add(new RegisterMessageCommand(
                        variablesId, msgDef.MessageDefinitionId, ActivityId, IsBoundary: false));
                    break;
                case SignalEventDef sigDef:
                    var signalDef = definition.GetSignalDefinition(sigDef.SignalDefinitionId);
                    commands.Add(new RegisterSignalCommand(
                        signalDef.Name, ActivityId, IsBoundary: false));
                    break;
                case TimerEventDef timerDef:
                    commands.Add(new RegisterTimerCommand(
                        ActivityId, timerDef.TimerDefinition.GetDueTime(), IsBoundary: false));
                    break;
            }
        }

        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlow = definition.GetOutgoingFlow(this);
        return Task.FromResult(nextFlow != null
            ? new List<ActivityTransition> { new(nextFlow.Target) }
            : new List<ActivityTransition>());
    }
}
