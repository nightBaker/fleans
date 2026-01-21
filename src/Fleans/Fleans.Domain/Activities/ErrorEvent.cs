


namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ErrorEvent : Activity
{
    public ErrorEvent(string ActivityId) : base(ActivityId)
    {
    }

    internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityState)
    {
        await base.ExecuteAsync(workflowInstance, activityState);
        await activityState.Complete();
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, IActivityInstance activityState)
    {
        var definition = await workflowInstance.GetWorkflowDefinition();
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();

    }
}
