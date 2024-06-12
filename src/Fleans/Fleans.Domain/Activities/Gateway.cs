

namespace Fleans.Domain.Activities
{
    public abstract class Gateway : Activity
    {        
        internal virtual async Task SetConditionResult(WorkflowInstance workflowInstance, ActivityInstance activityInstance, string conditionSeqenceFlowId, bool result)
        {
            var state = await workflowInstance.GetState();
            var seqState = await state.GetConditionSequenceStates();

            var sequences = seqState[activityInstance.ActivityInstanceId];

            var sequence = sequences.FirstOrDefault(s => s.ConditionalSequence.SequenceFlowId == conditionSeqenceFlowId);

            if (sequence != null)
            {
                sequence.SetResult(result);
            }
            else
            {
                throw new NullReferenceException("Sequence not found");
            }
        }
    }



}
