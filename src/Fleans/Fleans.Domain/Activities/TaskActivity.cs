

namespace Fleans.Domain.Activities
{
    public class TaskActivity : Activity
    {
        public override void Execute(WorkflowInstance workflowInstance, ActivityState activityState)
        {
            activityState.Execute();
        }

        public override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityState state)
        {
            var nextFlow = workflowInstance.Workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
            return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
        }
    }



}
