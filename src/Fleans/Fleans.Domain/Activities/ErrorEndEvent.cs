namespace Fleans.Domain.Activities;

/// <summary>
/// Error end event: ends its path by throwing a BPMN error (#843). The event fails itself with
/// <see cref="ErrorCode"/>, so the error reaches the nearest matching error boundary or error
/// event sub-process, or fails a call activity's parent, through the same path as a failed task.
/// <see cref="ErrorCode"/> is <c>null</c> when the event names no error; only catch-all handlers
/// catch it then.
/// </summary>
[GenerateSerializer]
public record ErrorEndEvent(
    string ActivityId,
    [property: Id(1)] string? ErrorCode) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        // Not completed: the aggregate fails this instance with the error.
        commands.Add(new ThrowErrorCommand(ErrorCode));
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
        => Task.FromResult(new List<ActivityTransition>());
}
