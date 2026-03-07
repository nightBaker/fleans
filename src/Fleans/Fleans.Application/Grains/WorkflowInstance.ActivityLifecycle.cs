using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Fleans.Application.Services;
using Fleans.Application.WorkflowFactory;
using Orleans;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    public async Task CompleteActivity(string activityId, ExpandoObject variables)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompletingActivity(activityId);
        await CompleteActivityState(activityId, variables);
        await ExecuteWorkflow();
        await _state.WriteStateAsync();
    }

    public async Task CompleteActivity(string activityId, Guid activityInstanceId, ExpandoObject variables)
    {
        // Stale callback guard: iteration may have been cancelled by MI host failure
        if (!State.Entries.Any(e => e.ActivityInstanceId == activityInstanceId && !e.IsCompleted))
        {
            LogStaleCallbackIgnored(activityId, activityInstanceId, "CompleteActivity");
            return;
        }

        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompletingActivity(activityId);
        await CompleteActivityState(activityId, variables, activityInstanceId);
        await ExecuteWorkflow();
        await _state.WriteStateAsync();
    }

    public async Task FailActivity(string activityId, Exception exception)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogFailingActivity(activityId);

        await FailActivityWithBoundaryCheck(activityId, exception);
        await _state.WriteStateAsync();
    }

    public async Task FailActivity(string activityId, Guid activityInstanceId, Exception exception)
    {
        // Stale callback guard: iteration may have been cancelled by MI host failure
        if (!State.Entries.Any(e => e.ActivityInstanceId == activityInstanceId && !e.IsCompleted))
        {
            LogStaleCallbackIgnored(activityId, activityInstanceId, "FailActivity");
            return;
        }

        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogFailingActivity(activityId);
        await FailActivityWithBoundaryCheck(activityId, exception, activityInstanceId);
        await _state.WriteStateAsync();
    }

    private async Task CompleteActivityState(string activityId, ExpandoObject variables, Guid? activityInstanceId = null)
    {
        var entry = activityInstanceId.HasValue
            ? State.GetActiveEntry(activityInstanceId.Value)
            : State.GetFirstActive(activityId)
                ?? throw new InvalidOperationException("Active activity not found");

        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
        SetActivityRequestContext(activityId, activityInstance);
        await activityInstance.Complete();
        var variablesId = await activityInstance.GetVariablesStateId();
        RequestContext.Set("VariablesId", variablesId.ToString());

        LogStateMergeState(variablesId);
        State.MergeState(variablesId, variables);

        // Unregister any boundary timer reminders attached to this activity
        var definition = await GetWorkflowDefinition();
        await _boundaryHandler.UnregisterBoundaryTimerRemindersAsync(activityId, entry.ActivityInstanceId, definition);

        // Unsubscribe any boundary message subscriptions attached to this activity
        await _boundaryHandler.UnsubscribeBoundaryMessageSubscriptionsAsync(activityId, variablesId, definition);

        // Unsubscribe any boundary signal subscriptions attached to this activity
        await _boundaryHandler.UnsubscribeBoundarySignalSubscriptionsAsync(activityId, definition);

        // Cancel sibling catch events if this activity is after an Event-Based Gateway
        await CancelEventBasedGatewaySiblings(activityId, definition);
    }

    private async Task CancelEventBasedGatewaySiblings(string completedActivityId, IWorkflowDefinition definition)
    {
        var siblingActivityIds = definition.GetEventBasedGatewaySiblings(completedActivityId);
        if (siblingActivityIds.Count == 0) return;

        foreach (var siblingId in siblingActivityIds)
        {
            var entry = State.GetFirstActive(siblingId);
            if (entry is null) continue;

            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
            var siblingActivity = definition.GetActivityAcrossScopes(siblingId);

            // Unsubscribe based on event type
            switch (siblingActivity)
            {
                case TimerIntermediateCatchEvent:
                    var callbackGrain = _grainFactory.GetGrain<ITimerCallbackGrain>(
                        this.GetPrimaryKey(), $"{entry.ActivityInstanceId}:{siblingId}");
                    await callbackGrain.Cancel();
                    break;

                case MessageIntermediateCatchEvent msgCatch:
                    var variablesId = await activityInstance.GetVariablesStateId();
                    var msgDef = definition.Messages.First(m => m.Id == msgCatch.MessageDefinitionId);
                    if (msgDef.CorrelationKeyExpression is not null)
                    {
                        var corrValue = await GetVariable(variablesId, msgDef.CorrelationKeyExpression);
                        if (corrValue is not null)
                        {
                            var grainKey = MessageCorrelationKey.Build(msgDef.Name, corrValue.ToString()!);
                            var corrGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
                            await corrGrain.Unsubscribe();
                        }
                    }
                    break;

                case SignalIntermediateCatchEvent sigCatch:
                    var sigDef = definition.Signals.First(s => s.Id == sigCatch.SignalDefinitionId);
                    var sigGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(sigDef.Name);
                    await sigGrain.Unsubscribe(this.GetPrimaryKey(), siblingId);
                    break;
            }

            await activityInstance.Cancel("Event-based gateway: sibling event completed");
            LogEventBasedGatewaySiblingCancelled(siblingId, completedActivityId);
        }
    }

    // Invariant: caller must ensure the entry is active before calling. For the
    // activityInstanceId overload, FailActivity's stale-callback guard checks this.
    private async Task FailActivityState(string activityId, Exception exception, Guid? activityInstanceId = null)
    {
        var entry = activityInstanceId.HasValue
            ? State.GetActiveEntry(activityInstanceId.Value)
            : State.GetFirstActive(activityId)
                ?? throw new InvalidOperationException("Active activity not found");

        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
        SetActivityRequestContext(activityId, activityInstance);
        await activityInstance.Fail(exception);
    }

    private async Task Complete()
    {
        State.Complete();
        LogStateCompleted();
        await _state.WriteStateAsync();

        // Notify parent if this is a child workflow
        if (State.ParentWorkflowInstanceId.HasValue)
        {
            var childVariables = State.VariableStates.Count > 0
                ? State.VariableStates[0].Variables
                : new ExpandoObject();

            var parent = _grainFactory.GetGrain<IWorkflowInstanceGrain>(State.ParentWorkflowInstanceId.Value);
            await parent.OnChildWorkflowCompleted(State.ParentActivityId!, childVariables);
        }
    }

    public async Task SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId)
    {
        State.SetParentInfo(parentWorkflowInstanceId, parentActivityId);
        LogParentInfoSet(parentWorkflowInstanceId, parentActivityId);
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

    private async Task StartChildWorkflow(CallActivity callActivity, IActivityExecutionContext activityContext)
    {
        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        var childDefinition = await factory.GetLatestWorkflowDefinition(callActivity.CalledProcessKey);

        var childId = Guid.NewGuid();
        var child = _grainFactory.GetGrain<IWorkflowInstanceGrain>(childId);

        LogStartingChildWorkflow(callActivity.CalledProcessKey, childId);

        await child.SetWorkflow(childDefinition);
        await child.SetParentInfo(this.GetPrimaryKey(), callActivity.ActivityId);

        // Map input variables
        var activityInstanceId = await activityContext.GetActivityInstanceId();
        var activityGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(activityInstanceId);
        var variablesId = await activityGrain.GetVariablesStateId();
        var parentVariables = State.GetVariableState(variablesId).Variables;
        var childInputVars = callActivity.BuildChildInputVariables(parentVariables);

        if (((IDictionary<string, object?>)childInputVars).Count > 0)
            await child.SetInitialVariables(childInputVars);

        // Record child reference on parent's activity entry
        var entry = State.GetFirstActive(callActivity.ActivityId)
            ?? throw new InvalidOperationException($"Active entry not found for '{callActivity.ActivityId}'");
        entry.SetChildWorkflowInstanceId(childId);

        // Start child — completion notifies parent via direct grain call
        await child.StartWorkflow();

        await _state.WriteStateAsync();
    }

    public async Task OnChildWorkflowCompleted(string parentActivityId, ExpandoObject childVariables)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogChildWorkflowCompleted(parentActivityId);

        var definition = await GetWorkflowDefinition();
        var callActivity = definition.GetActivityAcrossScopes(parentActivityId) as CallActivity
            ?? throw new InvalidOperationException($"Activity '{parentActivityId}' is not a CallActivity");

        var mappedOutput = callActivity.BuildParentOutputVariables(childVariables);
        await CompleteActivityState(parentActivityId, mappedOutput);
        await ExecuteWorkflow();
        await _state.WriteStateAsync();
    }

    public async Task OnChildWorkflowFailed(string parentActivityId, Exception exception)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogChildWorkflowFailed(parentActivityId);

        await FailActivityWithBoundaryCheck(parentActivityId, exception);
        await _state.WriteStateAsync();
    }

    public async Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogConditionResult(conditionSequenceId, result);

        var entry = State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);

        var definition = await GetWorkflowDefinition();
        var gateway = definition.GetActivityAcrossScopes(activityId) as ConditionalGateway
            ?? throw new InvalidOperationException("Activity is not a conditional gateway");

        bool isDecisionMade;
        try
        {
            isDecisionMade = await gateway.SetConditionResult(
                this, activityInstance, conditionSequenceId, result, definition);
        }
        catch (InvalidOperationException)
        {
            LogGatewayNoDefaultFlow(activityId);
            throw;
        }

        if (isDecisionMade)
        {
            LogGatewayAutoCompleting(activityId);
            await activityInstance.Complete();
            await ExecuteWorkflow();
        }

        await _state.WriteStateAsync();
    }

    // Called from FailActivityWithBoundaryCheck; caller handles ExecuteWorkflow() and WriteStateAsync().
    private async Task FailMultiInstanceHost(ActivityInstanceEntry failedIterationEntry, ActivityErrorState errorState)
    {
        var hostInstanceId = failedIterationEntry.ScopeId!.Value;
        var hostEntry = State.Entries.First(e => e.ActivityInstanceId == hostInstanceId);
        State.CompleteEntries([failedIterationEntry]);
        await CancelScopeChildren(hostInstanceId);
        var scopeEntries = State.Entries.Where(e => e.ScopeId == hostInstanceId).ToList();
        await CleanupMultiInstanceChildScopes(scopeEntries);
        var hostGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId);
        await hostGrain.Fail(new Exception(errorState.Message));
        LogMultiInstanceHostFailed(hostEntry.ActivityId, errorState.Message);
        State.CompleteEntries([hostEntry]);
    }

    private async Task FailActivityWithBoundaryCheck(string activityId, Exception exception, Guid? activityInstanceId = null)
    {
        await FailActivityState(activityId, exception, activityInstanceId);

        var definition = await GetWorkflowDefinition();
        var activityEntry = activityInstanceId.HasValue
            ? (State.GetActiveActivities().FirstOrDefault(e => e.ActivityInstanceId == activityInstanceId.Value)
               ?? State.Entries.Last(e => e.ActivityInstanceId == activityInstanceId.Value))
            : State.GetFirstActive(activityId) ?? State.Entries.Last(e => e.ActivityId == activityId);
        var activityGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(activityEntry.ActivityInstanceId);
        var errorState = await activityGrain.GetErrorState();

        if (errorState is null)
        {
            // errorState is never null after FailActivityState — defensive guard only
            await ExecuteWorkflow();
            return;
        }

        var match = definition.FindBoundaryErrorHandler(activityId, errorState.Code.ToString());
        if (match is null)
        {
            if (activityEntry.MultiInstanceIndex is not null)
            {
                // MI iteration failed — cancel siblings and fail host
                await FailMultiInstanceHost(activityEntry, errorState);
            }
            else
            {
                var failedEntry = activityInstanceId.HasValue
                    ? State.Entries.Last(e => e.ActivityInstanceId == activityInstanceId.Value)
                    : State.Entries.Last(e => e.ActivityId == activityId);
                State.CompleteEntries([failedEntry]);
            }

            await ExecuteWorkflow();

            // If this is a child workflow with no remaining active activities,
            // propagate the failure to the parent (mirrors Complete() success path)
            if (State.ParentWorkflowInstanceId.HasValue && !State.GetActiveActivities().Any())
            {
                LogChildFailurePropagatedToParent(State.ParentActivityId!);
                var parent = _grainFactory.GetGrain<IWorkflowInstanceGrain>(State.ParentWorkflowInstanceId.Value);
                await parent.OnChildWorkflowFailed(State.ParentActivityId!, exception);
            }

            return;
        }

        var (boundaryEvent, scopeDefinition, attachedToActivityId) = match.Value;

        if (attachedToActivityId == activityId)
        {
            // Direct match — boundary is on the failed activity itself
            await _boundaryHandler.HandleBoundaryErrorAsync(activityId, boundaryEvent, activityEntry.ActivityInstanceId, scopeDefinition);
        }
        else
        {
            // Bubbled up — cancel intermediate scopes between failed activity and matched SubProcess
            var failedEntry = activityInstanceId.HasValue
                ? State.Entries.Last(e => e.ActivityInstanceId == activityInstanceId.Value)
                : State.Entries.Last(e => e.ActivityId == activityId);
            State.CompleteEntries([failedEntry]);

            var currentScopeId = failedEntry.ScopeId;
            while (currentScopeId.HasValue)
            {
                var scopeEntry = State.Entries.First(e => e.ActivityInstanceId == currentScopeId.Value);

                if (scopeEntry.ActivityId == attachedToActivityId)
                {
                    // Found the SubProcess the boundary is attached to — cancel and handle
                    await CancelScopeChildren(currentScopeId.Value);
                    var scopeInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(scopeEntry.ActivityInstanceId);
                    await scopeInstance.Cancel("Sub-process interrupted by error boundary");
                    State.CompleteEntries([scopeEntry]);
                    await _boundaryHandler.HandleBoundaryErrorAsync(scopeEntry.ActivityId, boundaryEvent, scopeEntry.ActivityInstanceId, scopeDefinition);
                    return;
                }

                currentScopeId = scopeEntry.ScopeId;
            }
        }
    }
}
