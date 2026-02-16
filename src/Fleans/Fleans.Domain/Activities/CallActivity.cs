namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record CallActivity(
    string ActivityId,
    [property: Id(1)] string CalledProcessKey,
    [property: Id(2)] List<VariableMapping> InputMappings,
    [property: Id(3)] List<VariableMapping> OutputMappings,
    [property: Id(4)] bool PropagateAllParentVariables = true,
    [property: Id(5)] bool PropagateAllChildVariables = true) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        await base.ExecuteAsync(workflowContext, activityContext);
        await workflowContext.StartChildWorkflow(this, activityContext);
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
    }
}
