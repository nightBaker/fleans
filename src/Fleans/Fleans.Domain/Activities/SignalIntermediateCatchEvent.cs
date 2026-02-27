using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record SignalIntermediateCatchEvent(
    string ActivityId,
    [property: Id(1)] string SignalDefinitionId) : BoundarableActivity(ActivityId)
{
    internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
        var signalDef = definition.Signals.First(s => s.Id == SignalDefinitionId);
        commands.Add(new RegisterSignalCommand(signalDef.Name, ActivityId, IsBoundary: false));
        return commands;
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return Task.FromResult(nextFlow != null ? [nextFlow.Target] : new List<Activity>());
    }
}
