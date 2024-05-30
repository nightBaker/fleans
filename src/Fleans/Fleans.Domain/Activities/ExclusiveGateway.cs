
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities
{
    public class ExclusiveGateway : Gateway
    {
        public ExclusiveGateway(string activityId)
        {
            ActivityId = activityId;
        }

        internal override void Execute(WorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            base.Execute(workflowInstance, activityInstance);

            var sequences = AddConditionalSequencesToWorkflowInstance(workflowInstance, activityInstance);

            QueueEvaluteConditionEvents(workflowInstance, activityInstance, sequences);
        }

        private static IEnumerable<ConditionalSequenceFlow> AddConditionalSequencesToWorkflowInstance(WorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            var sequences = workflowInstance.Workflow.SequenceFlows.OfType<ConditionalSequenceFlow>()
                                    .Where(sf => sf.Source.ActivityId == activityInstance.CurrentActivity.ActivityId);

            workflowInstance.State.AddConditionSequenceStates(activityInstance.ActivityInstanceId, sequences);
            return sequences;
        }

        private void QueueEvaluteConditionEvents(WorkflowInstance workflowInstance, ActivityInstance activityInstance, IEnumerable<ConditionalSequenceFlow> sequences)
        {
            foreach (var sequence in sequences)
            {
                workflowInstance.EnqueueEvent(new EvaluateConditionEvent(workflowInstance.WorkflowInstanceId,
                                                                    workflowInstance.Workflow.WorkflowId,
                                                                    activityInstance.ActivityInstanceId,
                                                                    ActivityId,
                                                                    sequence.SequenceFlowId,
                                                                    sequence.Condition));
            }
        }

        internal override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance activityInstance)
        {
            return workflowInstance.State.ConditionSequenceStates[activityInstance.ActivityInstanceId]
                                .Where(x=>x.Result)
                                .Select(x=>x.ConditionalSequence.Target)
                                .ToList();           
        }
    }



}
