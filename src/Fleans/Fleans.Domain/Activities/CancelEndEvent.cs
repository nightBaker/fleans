namespace Fleans.Domain.Activities;

/// <summary>
/// BPMN Cancel End Event — thrown inside a Transaction Sub-Process to trigger cancellation.
/// When reached, the engine cancels all active activities in the transaction scope,
/// invokes compensation handlers in reverse order, then fires the CancelBoundaryEvent.
/// </summary>
[GenerateSerializer]
public record CancelEndEvent(string ActivityId) : Activity(ActivityId)
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

    // Terminal within the transaction — no outgoing flows.
    // The scope-completion handler detects it and triggers the cancel flow.
    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        return Task.FromResult(new List<ActivityTransition>());
    }
}
