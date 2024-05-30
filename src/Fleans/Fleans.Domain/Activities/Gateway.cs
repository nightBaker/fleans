

namespace Fleans.Domain.Activities
{
    public abstract class Gateway : Activity
    {        
        internal virtual void SetConditionResult(WorkflowInstance workflowInstance, ActivityInstance activityInstance, string conditionSeqenceFlowId, bool result)
        {
            var sequences = workflowInstance.State.ConditionSequenceStates[activityInstance.ActivityInstanceId];

            var sequence = sequences.FirstOrDefault(s => s.ConditionalSequence.SequenceFlowId == conditionSeqenceFlowId);

            if (sequence != null)
            {
                sequence.SetResult(result);
            }
            else
            {
                throw new Exception("Sequence not found");
            }
        }
    }



}
