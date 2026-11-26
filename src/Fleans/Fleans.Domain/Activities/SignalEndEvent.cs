namespace Fleans.Domain.Activities;

/// <summary>
/// Signal end event (#843): broadcasts its signal, like a signal intermediate throw event, and
/// ends its path like a plain end event.
/// </summary>
[GenerateSerializer]
public record SignalEndEvent(
    string ActivityId,
    [property: Id(1)] string SignalDefinitionId) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        var signalDef = definition.GetSignalDefinition(SignalDefinitionId);
        commands.Add(new ThrowSignalCommand(signalDef.Name));
        await activityContext.Complete();
        if (definition.IsRootScope)
            commands.Add(new CompleteWorkflowCommand());
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
        => Task.FromResult(new List<ActivityTransition>());
}
