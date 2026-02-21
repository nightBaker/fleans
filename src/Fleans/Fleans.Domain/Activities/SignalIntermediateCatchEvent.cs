using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record SignalIntermediateCatchEvent(
    string ActivityId,
    [property: Id(1)] string SignalDefinitionId) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        await base.ExecuteAsync(workflowContext, activityContext, definition);
        await workflowContext.RegisterSignalSubscription(SignalDefinitionId, ActivityId);
        // Do NOT call activityContext.Complete() — the signal grain will do that
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
