


namespace Fleans.Domain.Activities
{
    public class ErrorEvent : Activity
    {
        internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            await base.ExecuteAsync(workflowInstance, activityState);
            activityState.Complete();
        }

        internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            var definition = await workflowInstance.GetWorkflowDefinition();
            var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
            return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();

        }
    }
}
