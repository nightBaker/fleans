

namespace Fleans.Domain.Activities
{
    public class ErrorEvent : Activity
    {
        public override void Execute(WorkflowInstance workflowInstance, ActivityState activityState)
        {
            activityState.Complete();            
        }

        public override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityState activityState)
        {
            var nextFlow = workflowInstance.Workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
            return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
        }
    }
}
