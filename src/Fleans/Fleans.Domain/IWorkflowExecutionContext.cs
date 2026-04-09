using Fleans.Domain.States;

namespace Fleans.Domain;

public interface IWorkflowExecutionContext
{
    ValueTask<Guid> GetWorkflowInstanceId();

    ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
    ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result);

    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();
    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();

    ValueTask<object?> GetVariable(Guid variablesId, string variableName);

    ValueTask<GatewayForkState?> FindForkByToken(Guid tokenId);

    ValueTask<ComplexGatewayJoinState?> GetComplexGatewayJoinState(Guid activityInstanceId);
    ValueTask<ComplexGatewayJoinState> GetOrCreateComplexGatewayJoinState(Guid activityInstanceId, string activationCondition);
}
