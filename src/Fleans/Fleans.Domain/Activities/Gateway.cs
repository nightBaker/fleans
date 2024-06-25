

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
        var activityInstanceId = await activityInstance.GetActivityInstanceId();

        state.SetCondigitionSequencesResult(activityInstanceId, conditionSeqenceFlowId, result);
    }
}
