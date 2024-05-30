

namespace Fleans.Domain.Activities
{
    public class StartEvent : Activity
    {
        public StartEvent(string activityId)
        {
            ActivityId = activityId;
        }

        internal override void Execute(WorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            base.Execute(workflowInstance, activityInstance);

            activityInstance.Complete();
            workflowInstance.Start();
            
        }

        internal override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            var nextFlow = workflowInstance.Workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
            return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
        }
    }
}
