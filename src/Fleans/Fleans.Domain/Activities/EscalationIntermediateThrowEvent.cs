namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record EscalationIntermediateThrowEvent(
    string ActivityId,
    [property: Id(1)] string EscalationCode) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        commands.Add(new ThrowEscalationCommand(EscalationCode));
        await activityContext.Complete();
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
