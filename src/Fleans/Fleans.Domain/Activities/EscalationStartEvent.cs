namespace Fleans.Domain.Activities;

/// <summary>
/// Escalation start event — only valid inside an <see cref="EventSubProcess"/> (#783). Catches
/// escalations thrown in the enclosing scope (and propagated from its nested scopes and call
/// activities). Interrupting or not, as the event sub-process says. <see cref="EscalationCode"/>
/// is <c>null</c> for a catch-all handler.
/// </summary>
[GenerateSerializer]
public record EscalationStartEvent(
    string ActivityId,
    [property: Id(1)] string? EscalationCode) : Activity(ActivityId)
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
        var nextFlow = definition.GetOutgoingFlow(this);
        return Task.FromResult(nextFlow != null
            ? new List<ActivityTransition> { new(nextFlow.Target) }
            : new List<ActivityTransition>());
    }
}
