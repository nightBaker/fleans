

using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record StartEvent : Activity
{
    public StartEvent(string activityId): base(activityId)
    {
        ActivityId = activityId;
    }
    internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        await base.ExecuteAsync(workflowInstance, activityInstance);

        await activityInstance.Complete();
        await workflowInstance.Start();
        
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        var defition = await workflowInstance.GetWorkflowDefinition();
        var nextFlow = defition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
    }
}
