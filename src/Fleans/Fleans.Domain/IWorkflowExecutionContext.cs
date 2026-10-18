using Fleans.Domain.States;

namespace Fleans.Domain;

public interface IWorkflowExecutionContext
{
    ValueTask<Guid> GetWorkflowInstanceId();

    /// <summary>The instance's business identifier set at start, or <c>null</c> (#824).</summary>
    ValueTask<string?> GetBusinessId();

    ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
    ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result);

    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();
    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();

    ValueTask<object?> GetVariable(Guid variablesId, string variableName);

    ValueTask<GatewayForkState?> FindForkByToken(Guid tokenId);

    ValueTask<ComplexGatewayJoinState?> GetComplexGatewayJoinState(string gatewayActivityId);
}
