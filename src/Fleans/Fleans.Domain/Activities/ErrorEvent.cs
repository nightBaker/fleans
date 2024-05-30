

namespace Fleans.Domain.Activities
{
    public class ErrorEvent : Activity
    {
        internal override void Execute(WorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            base.Execute(workflowInstance, activityState);
            activityState.Complete();            
        }

        internal override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            var nextFlow = workflowInstance.Workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
            return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
        }
    }
}
