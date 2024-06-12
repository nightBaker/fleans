
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Orleans;
using Orleans.Runtime;

namespace Fleans.Domain.Activities
{
    public class ExclusiveGateway : Gateway
    {
        public ExclusiveGateway(string activityId)
        {
            ActivityId = activityId;
        }

        internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            await base.ExecuteAsync(workflowInstance, activityInstance);

            var sequences = await AddConditionalSequencesToWorkflowInstance(workflowInstance, activityInstance);

            await QueueEvaluteConditionEvents(workflowInstance, activityInstance, sequences);

        }

        private static async Task<IEnumerable<ConditionalSequenceFlow>> AddConditionalSequencesToWorkflowInstance(IWorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            var sequences = (await workflowInstance.GetWorkflowDefinition()).SequenceFlows.OfType<ConditionalSequenceFlow>()
                                    .Where(sf => sf.Source.ActivityId == activityInstance.CurrentActivity.ActivityId);

            var state = await workflowInstance.GetState();
            state.AddConditionSequenceStates(activityInstance.ActivityInstanceId, sequences);
            return sequences;
        }

        private async Task QueueEvaluteConditionEvents(IWorkflowInstance workflowInstance, ActivityInstance activityInstance, IEnumerable<ConditionalSequenceFlow> sequences)
        {
            var definition = await workflowInstance.GetWorkflowDefinition();
            foreach (var sequence in sequences)
            {
                
                workflowInstance.EnqueueEvent(new EvaluateConditionEvent(workflowInstance.GetGrainId().GetGuidKey(),
                                                                   definition.WorkflowId,
                                                                    activityInstance.ActivityInstanceId,
                                                                    ActivityId,
                                                                    sequence.SequenceFlowId,
                                                                    sequence.Condition));
            }
        }

        internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            var state = await workflowInstance.GetState();
            var sequencesState = await state.GetConditionSequenceStates();
            return sequencesState[activityInstance.ActivityInstanceId]
                                .Where(x=>x.Result)
                                .Select(x=>x.ConditionalSequence.Target)
                                .ToList();           
        }
    }



}
