

namespace Fleans.Domain.Activities
{
    public class TaskActivity : Activity
    {
        internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, ActivityInstance state)
        {
            var definition = await workflowInstance.GetWorkflowDefinition();
            var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
            return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
        }
    }
}
