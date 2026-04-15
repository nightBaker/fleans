using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record MultipleIntermediateThrowEvent(
    string ActivityId,
    [property: Id(1)] IReadOnlyList<EventDefinition> Definitions) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        foreach (var def in Definitions)
        {
            switch (def)
            {
                case SignalEventDef sigDef:
                    var signalDef = definition.GetSignalDefinition(sigDef.SignalDefinitionId);
                    commands.Add(new ThrowSignalCommand(signalDef.Name));
                    break;
            }
        }

        await activityContext.Complete();
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
