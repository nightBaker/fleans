using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record BoundaryTimerEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] TimerDefinition TimerDefinition) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        await base.ExecuteAsync(workflowContext, activityContext);
        await activityContext.Complete();
    }

    internal override async Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
    }
}
