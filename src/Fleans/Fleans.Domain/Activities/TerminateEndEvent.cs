namespace Fleans.Domain.Activities;

/// <summary>
/// Terminate end event (#843): ends its scope at once. Every other active path of the scope is
/// cancelled; at root level the instance then completes, inside a sub-process the sub-process
/// completes and its parent carries on.
/// </summary>
[GenerateSerializer]
public record TerminateEndEvent(string ActivityId) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        await activityContext.Complete();
        commands.Add(new TerminateScopeCommand());
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
