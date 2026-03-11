namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ErrorEvent(string ActivityId) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        await activityContext.Complete();
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var nextFlow = definition.GetOutgoingFlow(this);
        return Task.FromResult(nextFlow != null ? new List<ActivityTransition> { new(nextFlow.Target) } : new List<ActivityTransition>());
    }
}
