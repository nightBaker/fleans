using Fleans.Domain;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
using Fleans.Application.Adapters;
using Fleans.Application.WorkflowFactory;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    public async Task CompleteActivity(string activityId, ExpandoObject variables)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompletingActivity(activityId);

        var effects = _execution!.CompleteActivity(activityId, null, variables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task CompleteActivity(string activityId, Guid activityInstanceId, ExpandoObject variables)
    {
        await EnsureExecution();

        // Stale callback guard
        if (!State.Entries.Any(e => e.ActivityInstanceId == activityInstanceId && !e.IsCompleted))
        {
            LogStaleCallbackIgnored(activityId, activityInstanceId, "CompleteActivity");
            return;
        }

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompletingActivity(activityId);

        var effects = _execution!.CompleteActivity(activityId, activityInstanceId, variables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task FailActivity(string activityId, Exception exception)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogFailingActivity(activityId);

        var effects = _execution!.FailActivity(activityId, null, exception);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task FailActivity(string activityId, Guid activityInstanceId, Exception exception)
    {
        await EnsureExecution();

        // Stale callback guard
        if (!State.Entries.Any(e => e.ActivityInstanceId == activityInstanceId && !e.IsCompleted))
        {
            LogStaleCallbackIgnored(activityId, activityInstanceId, "FailActivity");
            return;
        }

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogFailingActivity(activityId);

        var effects = _execution!.FailActivity(activityId, activityInstanceId, exception);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogConditionResult(conditionSequenceId, result);

        _execution!.CompleteConditionSequence(activityId, conditionSequenceId, result);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId)
    {
        await EnsureExecution();
        _execution!.SetParentInfo(parentWorkflowInstanceId, parentActivityId);
        LogParentInfoSet(parentWorkflowInstanceId, parentActivityId);
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task SetInitialVariables(ExpandoObject variables)
    {
        if (State.VariableStates.Count == 0)
            throw new InvalidOperationException("Call SetWorkflow before SetInitialVariables.");

        State.MergeState(State.VariableStates[0].Id, variables);
        LogInitialVariablesSet();
        await _state.WriteStateAsync();
    }

    public async Task OnChildWorkflowCompleted(string parentActivityId, ExpandoObject childVariables)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogChildWorkflowCompleted(parentActivityId);

        var effects = _execution!.OnChildWorkflowCompleted(parentActivityId, childVariables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task OnChildWorkflowFailed(string parentActivityId, Exception exception)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogChildWorkflowFailed(parentActivityId);

        var effects = _execution!.OnChildWorkflowFailed(parentActivityId, exception);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }
}
