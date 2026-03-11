using Fleans.Domain;
using Fleans.Domain.Effects;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    public async Task HandleTimerFired(string timerActivityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogTimerReminderFired(timerActivityId);

        var effects = _execution!.HandleTimerFired(timerActivityId, hostActivityInstanceId);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task HandleMessageDelivery(string activityId, Guid hostActivityInstanceId, ExpandoObject variables)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var effects = _execution!.HandleMessageDelivery(activityId, hostActivityInstanceId, variables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task HandleBoundaryMessageFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        // HandleMessageDelivery on the aggregate handles both boundary and non-boundary
        var effects = _execution!.HandleMessageDelivery(boundaryActivityId, hostActivityInstanceId, new ExpandoObject());
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var effects = _execution!.HandleSignalDelivery(activityId, hostActivityInstanceId);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var effects = _execution!.HandleSignalDelivery(boundaryActivityId, hostActivityInstanceId);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }
}
