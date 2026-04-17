namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record EscalationEndEvent(
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
        if (definition.IsRootScope)
            commands.Add(new CompleteWorkflowCommand());
        await activityContext.Complete();
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        return Task.FromResult(new List<ActivityTransition>());
    }
}
