using Fleans.Domain.States;

namespace Fleans.Domain;

public interface IWorkflowExecutionContext
{
    ValueTask<Guid> GetWorkflowInstanceId();
    ValueTask Complete();
    ValueTask StartChildWorkflow(Activities.CallActivity callActivity, IActivityExecutionContext activityContext);

    ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
    ValueTask AddConditionSequenceStates(Guid activityInstanceId, string[] sequenceFlowIds);
    ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result);

    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();
    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();

    ValueTask<object?> GetVariable(Guid variablesId, string variableName);
    ValueTask RegisterMessageSubscription(Guid variablesId, string messageDefinitionId, string activityId);
    ValueTask RegisterTimerReminder(Guid hostActivityInstanceId, string timerActivityId, TimeSpan dueTime);
    ValueTask RegisterBoundaryMessageSubscription(Guid variablesId, Guid hostActivityInstanceId, string boundaryActivityId, string messageDefinitionId);
    ValueTask RegisterSignalSubscription(string signalName, string activityId, Guid activityInstanceId);
    ValueTask RegisterBoundarySignalSubscription(Guid hostActivityInstanceId, string boundaryActivityId, string signalName);
    ValueTask ThrowSignal(string signalName);
}
