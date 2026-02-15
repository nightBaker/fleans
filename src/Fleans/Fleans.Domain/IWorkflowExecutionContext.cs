using Fleans.Domain.States;

namespace Fleans.Domain;

public interface IWorkflowExecutionContext
{
    ValueTask<Guid> GetWorkflowInstanceId();
    ValueTask<IWorkflowDefinition> GetWorkflowDefinition();
    ValueTask Complete();

    ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
    ValueTask AddConditionSequenceStates(Guid activityInstanceId, string[] sequenceFlowIds);
    ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result);

    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();
    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();
}
