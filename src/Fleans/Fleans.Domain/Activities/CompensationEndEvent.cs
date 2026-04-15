namespace Fleans.Domain.Activities;

/// <summary>
/// Compensation End Event — triggers compensation for the current scope (broadcast, no target)
/// and ends the scope. Semantically equivalent to a Compensation Intermediate Throw followed
/// by an End Event. Has no outgoing sequence flow.
/// </summary>
[GenerateSerializer]
public record CompensationEndEvent(string ActivityId) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        var throwerInstanceId = await activityContext.GetActivityInstanceId();
        // Broadcast compensation (no target activity)
        commands.Add(new CompensationRequestedCommand(throwerInstanceId, null));
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
