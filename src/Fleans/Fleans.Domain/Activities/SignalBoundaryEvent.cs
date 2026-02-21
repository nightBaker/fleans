using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
// TODO: Support non-interrupting boundary signal events (cancelActivity=false).
// Requires keeping the host activity active and running the boundary path in parallel.
public record SignalBoundaryEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] string SignalDefinitionId) : Activity(ActivityId)
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
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return Task.FromResult(nextFlow != null ? [nextFlow.Target] : new List<Activity>());
    }
}
