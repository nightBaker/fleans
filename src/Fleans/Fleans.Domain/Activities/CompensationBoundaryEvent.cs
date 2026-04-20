namespace Fleans.Domain.Activities;

/// <summary>
/// Compensation Boundary Event — attached to a completed activity, non-interrupting by definition.
/// Invoked only during a compensation walk; never fired on normal event delivery.
/// The <see cref="HandlerActivityId"/> is resolved at BPMN parse time from the &lt;association&gt;
/// element linking this boundary event to its compensation handler activity.
/// </summary>
[GenerateSerializer]
public record CompensationBoundaryEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] string HandlerActivityId) : Activity(ActivityId)
{
    // Compensation boundaries are never runtime-triggered via the execution loop.
    // ExecuteAsync should never be called on this activity type.
    internal override Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
        => throw new InvalidOperationException(
            $"CompensationBoundaryEvent '{ActivityId}' must not be executed via the normal execution loop. " +
            "It is invoked only through the compensation walk.");

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
        => Task.FromResult(new List<ActivityTransition>());
}
