

using Fleans.Domain.States;

namespace Fleans.Domain.Activities
{
    public class ParallelGateway : Gateway
    {
        public bool IsFork { get; }

        public ParallelGateway(bool isFork)
        {
            IsFork = isFork;
        }

        internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            if (IsFork)
            {
                activityState.Complete();
            }
            else
            {
                if (await AllIncomingPathsCompleted(await workflowInstance.GetState(), await workflowInstance.GetWorkflowDefinition()))
                {
                    activityState.Complete();
                }
                else
                {
                    activityState.Execute();
                }                
            }

            await base.ExecuteAsync(workflowInstance, activityState);
                
        }

        internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, ActivityInstance state)
        {
            var definition = await workflowInstance.GetWorkflowDefinition();
            var nextFlows = definition.SequenceFlows.Where(sf => sf.Source == this)
                                            .Select(flow => flow.Target)
                                            .ToList();
            
            return nextFlows;
        }

        private async Task<bool> AllIncomingPathsCompleted(IWorkflowInstanceState state, IWorkflowDefinition workflow)
        {
            var incomingFlows = workflow.SequenceFlows.Where(sf => sf.Target == this).ToList();

            var completedActivities = await state.GetCompletedActivities();
            var activeActivities = await state.GetActiveActivities();

            return incomingFlows.All(flow => completedActivities.Where(ca => ca.CurrentActivity == flow.Source)
                                                                    .All(ca => ca.IsCompleted))
                && incomingFlows.All(flow => activeActivities.Any(ca => ca.CurrentActivity == flow.Source));

        }
    }



}
