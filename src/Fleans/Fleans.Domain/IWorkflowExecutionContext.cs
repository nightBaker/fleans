using Fleans.Domain.States;

namespace Fleans.Domain;

public interface IWorkflowExecutionContext
{
    ValueTask<Guid> GetWorkflowInstanceId();
    ValueTask Complete();

    ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
    ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result);

    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();
    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();

    ValueTask<object?> GetVariable(Guid variablesId, string variableName);
}
