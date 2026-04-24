namespace Fleans.Domain.Activities;

/// <summary>
/// Compensation End Event — triggers compensation for the current scope and ends it.
/// Optionally targets a specific activity (activityRef) or broadcasts to all compensable
/// activities in the scope when TargetActivityRef is null.
/// Like the intermediate throw, this does NOT complete itself — the walk state machine
/// completes it after all handlers finish. The scope terminates after the walk.
/// </summary>
[GenerateSerializer]
public record CompensationEndEvent(
    string ActivityId,
    [property: Id(1)] string? TargetActivityRef = null) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        var throwerInstanceId = await activityContext.GetActivityInstanceId();
        commands.Add(new CompensationRequestedCommand(throwerInstanceId, TargetActivityRef));
        // Do NOT complete here — the walk state machine completes us.
        // CompleteWorkflowCommand is emitted after the walk completes the thrower,
        // detected by HandleScopeCompletions when no active entries remain.
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
        => Task.FromResult(new List<ActivityTransition>());
}
