

namespace Fleans.Domain.Activities
{
    public class StartEvent : Activity
    {
        public StartEvent(string activityId)
        {
            ActivityId = activityId;
        }
        internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            await base.ExecuteAsync(workflowInstance, activityInstance);

            activityInstance.Complete();
            workflowInstance.Start();
            
        }

        internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            var defition = await workflowInstance.GetWorkflowDefinition();
            var nextFlow = defition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
            return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
        }
    }
}
