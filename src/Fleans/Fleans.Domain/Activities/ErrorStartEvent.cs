namespace Fleans.Domain.Activities;

/// <summary>
/// Error start event — only valid inside an <see cref="EventSubProcess"/>. Catches
/// errors bubbling up from sibling activities in the parent scope. Per BPMN 2.0
/// §10.2.4 an error start event is always interrupting; the containing
/// <see cref="EventSubProcess.IsInterrupting"/> is forced to <c>true</c> by the parser.
///
/// <see cref="ErrorCode"/> is <c>null</c> for a catch-all handler.
///
/// Slice #A (this PR): domain type + parser only. Reactive discovery during error
/// bubbling is added in slice #B (see issue #227).
/// </summary>
[GenerateSerializer]
public record ErrorStartEvent(
    string ActivityId,
    [property: Id(1)] string? ErrorCode) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        await activityContext.Complete();
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        // Slice #A: not yet executed by the engine. When slice #B (error event
        // sub-process trigger) lands, the start event will simply complete and
        // hand off to its outgoing flow inside the event sub-process scope.
        var nextFlow = definition.GetOutgoingFlow(this);
        return Task.FromResult(nextFlow != null
            ? new List<ActivityTransition> { new(nextFlow.Target) }
            : new List<ActivityTransition>());
    }
}
