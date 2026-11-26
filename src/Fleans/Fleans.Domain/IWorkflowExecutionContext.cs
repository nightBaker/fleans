using Fleans.Domain.States;

namespace Fleans.Domain;

public interface IWorkflowExecutionContext
{
    ValueTask<Guid> GetWorkflowInstanceId();

    /// <summary>The instance's business identifier set at start, or <c>null</c> (#824).</summary>
    ValueTask<string?> GetBusinessId();

    ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();

    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();
    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();

    ValueTask<object?> GetVariable(Guid variablesId, string variableName);

    ValueTask<GatewayForkState?> FindForkByToken(Guid tokenId);

    /// <summary>The complex-join state of the scope the given join entry runs in (#839).</summary>
    ValueTask<ComplexGatewayJoinState?> GetComplexGatewayJoinState(Guid joinActivityInstanceId);
}
