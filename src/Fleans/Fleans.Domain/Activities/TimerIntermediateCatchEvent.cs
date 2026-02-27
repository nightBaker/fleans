using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record TimerIntermediateCatchEvent(
    string ActivityId,
    [property: Id(1)] TimerDefinition TimerDefinition) : BoundarableActivity(ActivityId)
{
    internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
        commands.Add(new RegisterTimerCommand(ActivityId, TimerDefinition.GetDueTime(), IsBoundary: false));
        return commands;
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
    }
}
