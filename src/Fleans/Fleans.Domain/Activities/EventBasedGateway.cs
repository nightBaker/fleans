namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record EventBasedGateway(string ActivityId) : Gateway(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        await base.ExecuteAsync(workflowContext, activityContext, definition);
        await activityContext.Complete();
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlows = definition.SequenceFlows
            .Where(sf => sf.Source == this)
            .Select(flow => flow.Target)
            .ToList();
        return Task.FromResult(nextFlows);
    }
}
