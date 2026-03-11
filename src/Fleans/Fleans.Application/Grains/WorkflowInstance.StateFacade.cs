using Fleans.Domain;
using Fleans.Domain.States;
using Fleans.Application.Adapters;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    public ValueTask<object?> GetVariable(Guid variablesId, string variableName)
        => ValueTask.FromResult(State.GetVariable(variablesId, variableName));

    public ValueTask<ExpandoObject> GetVariables(Guid variablesStateId)
        => ValueTask.FromResult(State.GetMergedVariables(variablesStateId));

    public ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities()
    {
        IReadOnlyList<IActivityExecutionContext> result = State.GetActiveActivities()
            .Select(e => (IActivityExecutionContext)new ActivityExecutionContextAdapter(e))
            .ToList().AsReadOnly();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities()
    {
        IReadOnlyList<IActivityExecutionContext> result = State.GetCompletedActivities()
            .Select(e => (IActivityExecutionContext)new ActivityExecutionContextAdapter(e))
            .ToList().AsReadOnly();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates()
    {
        IReadOnlyDictionary<Guid, ConditionSequenceState[]> result = State.ConditionSequenceStates
            .GroupBy(c => c.GatewayActivityInstanceId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        return ValueTask.FromResult(result);
    }

    public async ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        State.SetConditionSequenceResult(activityInstanceId, sequenceId, result);
        await _state.WriteStateAsync();
    }

    public ValueTask<GatewayForkState?> FindForkByToken(Guid tokenId)
        => ValueTask.FromResult(State.FindForkByToken(tokenId));
}
