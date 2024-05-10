
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities
{
    public class ExclusiveGateway : Gateway
    {
        private readonly List<ConditionalSequenceFlow> _flows = new List<ConditionalSequenceFlow>();

        public void AddConditionalFlow(ConditionalSequenceFlow flow)
        {
            _flows.Add(flow);
        }

        public override void Execute(WorkflowInstance workflowInstance, ActivityState activityState)
        {
            activityState.Complete();            
        }

        public override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityState state)
        {
            foreach (var flow in _flows)
            {
                if (flow.Condition.Evaluate())
                {
                    return new List<Activity> { flow.Target };
                }
            }
            return new List<Activity>();
        }
    }



}
