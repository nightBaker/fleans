
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities
{
    public class ExclusiveGateway : Gateway
    {
        public const string NextActivityIdKey = "_NextActivityId";

        public ExclusiveGateway(string activityId) 
        { 
            ActivityId = activityId;
        }
              
        public override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {    
            var nextActivityIdVariableKey = activityInstance.ActivityInstanceId + NextActivityIdKey;
            var nextActivityId = workflowInstance.State.VariableStates[activityInstance.VariablesStateId].Variables[nextActivityIdVariableKey] as string;

            if (!string.IsNullOrWhiteSpace(nextActivityId) 
                && workflowInstance.Workflow.SequenceFlows.Any(sf => sf.Target.ActivityId == nextActivityId))
            {
                var nextActivity = workflowInstance.Workflow.Activities.First(a => a.ActivityId == nextActivityId);

                return new List<Activity> { nextActivity };
            }
            else
            {
                return new List<Activity>();
            }
        }
    }



}
