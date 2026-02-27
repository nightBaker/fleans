using Fleans.Domain;
using Fleans.Domain.States;
using Fleans.Application.Services;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    public ValueTask<object?> GetVariable(Guid variablesId, string variableName)
        => ValueTask.FromResult(State.GetVariable(variablesId, variableName));

    public ValueTask<ExpandoObject> GetVariables(Guid variablesStateId)
        => ValueTask.FromResult(State.GetMergedVariables(variablesStateId));

    // State facade methods — activities access state through these, not directly
    public ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities()
    {
        IReadOnlyList<IActivityExecutionContext> result = State.GetActiveActivities()
            .Select(e => (IActivityExecutionContext)_grainFactory.GetGrain<IActivityInstanceGrain>(e.ActivityInstanceId))
            .ToList().AsReadOnly();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities()
    {
        IReadOnlyList<IActivityExecutionContext> result = State.GetCompletedActivities()
            .Select(e => (IActivityExecutionContext)_grainFactory.GetGrain<IActivityInstanceGrain>(e.ActivityInstanceId))
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

    private async Task AddConditionSequenceStates(Guid activityInstanceId, string[] sequenceFlowIds)
    {
        State.AddConditionSequenceStates(activityInstanceId, sequenceFlowIds);
        await _state.WriteStateAsync();
    }

    public async ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        State.SetConditionSequenceResult(activityInstanceId, sequenceId, result);
        await _state.WriteStateAsync();
    }
}
