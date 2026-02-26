using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record TimerIntermediateCatchEvent(
    string ActivityId,
    [property: Id(1)] TimerDefinition TimerDefinition) : BoundarableActivity(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        await base.ExecuteAsync(workflowContext, activityContext, definition);
        var hostInstanceId = await activityContext.GetActivityInstanceId();
        await workflowContext.RegisterTimerReminder(hostInstanceId, ActivityId, TimerDefinition.GetDueTime());
        // Do NOT call activityContext.Complete() — the reminder will do that
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
