namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ErrorEvent(string ActivityId) : Activity(ActivityId)
{
    internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
        commands.Add(new CompleteCommand());
        return commands;
    }

    internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
    }
}
