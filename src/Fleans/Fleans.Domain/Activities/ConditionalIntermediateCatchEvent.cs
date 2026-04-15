using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ConditionalIntermediateCatchEvent(
    string ActivityId,
    [property: Id(1)] string ConditionExpression) : BoundarableActivity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        var variablesId = await activityContext.GetVariablesStateId();
        commands.Add(new RegisterConditionalWatcherCommand(variablesId, ConditionExpression, ActivityId));
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlow = definition.GetOutgoingFlow(this);
        return Task.FromResult(nextFlow != null ? new List<ActivityTransition> { new(nextFlow.Target) } : new List<ActivityTransition>());
    }
}
