

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Gateway : Activity
{
    protected Gateway(string ActivityId) : base(ActivityId)
    {
    }

    internal virtual async Task SetConditionResult(WorkflowInstance workflowInstance, IActivityInstance activityInstance, string conditionSeqenceFlowId, bool result)
    {
        var state = await workflowInstance.GetState();
        var seqState = await state.GetConditionSequenceStates();

        var sequences = seqState[await activityInstance.GetActivityInstanceId()];

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
