

namespace Fleans.Domain.Activities
{
    public class ParallelGateway : Gateway
    {
        public bool IsFork { get; }

        public ParallelGateway(bool isFork)
        {
            IsFork = isFork;
        }

        public override void Execute(WorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            if (IsFork)
            {
                activityState.Complete();
            }
            else
            {
                if (AllIncomingPathsCompleted(workflowInstance.State, workflowInstance.Workflow))
                {
                    activityState.Complete();
                }
                else
                {
                    activityState.Execute();
                }                
            }

            base.Execute(workflowInstance, activityState);
                
        }

        public override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance state)
        {
            var nextFlows = workflowInstance.Workflow.SequenceFlows.Where(sf => sf.Source == this)
                                            .Select(flow => flow.Target)
                                            .ToList();
            
            return nextFlows;
        }

        private bool AllIncomingPathsCompleted(WorkflowInstanceState state, Workflow workflow)
        {
            var incomingFlows = workflow.SequenceFlows.Where(sf => sf.Target == this).ToList();

            return incomingFlows.All(flow => state.CompletedActivities.Where(ca => ca.CurrentActivity == flow.Source)
                                                                    .All(ca => ca.IsCompleted))
                && incomingFlows.All(flow => state.ActiveActivities.Any(ca => ca.CurrentActivity == flow.Source));

        }
    }



}
