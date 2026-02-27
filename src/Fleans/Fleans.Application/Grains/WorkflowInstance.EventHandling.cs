using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Application.Services;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    public async Task HandleTimerFired(string timerActivityId, Guid hostActivityInstanceId)
    {
        await EnsureWorkflowDefinitionAsync();
        var definition = await GetWorkflowDefinition();

        // Check if this is a boundary timer
        var scopeDef = definition.FindScopeForActivity(timerActivityId);
        var activity = scopeDef?.GetActivity(timerActivityId);
        if (activity is BoundaryTimerEvent boundaryTimer)
        {
            SetWorkflowRequestContext();
            using var scope = BeginWorkflowScope();
            LogTimerReminderFired(timerActivityId);
            await HandleBoundaryTimerFired(boundaryTimer, hostActivityInstanceId);
            await _state.WriteStateAsync();
        }
        else
        {
            SetWorkflowRequestContext();
            using var scope = BeginWorkflowScope();
            LogTimerReminderFired(timerActivityId);

            // Intermediate catch timer — just complete the activity
            // Guard: activity may already be completed by a previous reminder tick
            var entry = State.Entries.FirstOrDefault(e =>
                e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
            if (entry == null)
            {
                LogStaleTimerIgnored(timerActivityId);
                return;
            }

            await CompleteActivityState(timerActivityId, new ExpandoObject());
            await ExecuteWorkflow();
            await _state.WriteStateAsync();
        }
    }

    private Task HandleBoundaryTimerFired(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId)
        => _boundaryHandler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostActivityInstanceId, _workflowDefinition!);

    public async Task HandleMessageDelivery(string activityId, Guid hostActivityInstanceId, ExpandoObject variables)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var definition = await GetWorkflowDefinition();
        var scopeDef = definition.GetScopeForActivity(activityId);
        var activity = scopeDef.GetActivity(activityId);

        if (activity is MessageBoundaryEvent boundaryMessage)
        {
            LogMessageDeliveryBoundary(activityId);
            await _boundaryHandler.HandleBoundaryMessageFiredAsync(boundaryMessage, hostActivityInstanceId, definition);
        }
        else
        {
            LogMessageDeliveryComplete(activityId);
            await CompleteActivityState(activityId, variables);
            await ExecuteWorkflow();
        }

        await _state.WriteStateAsync();
    }

    public async Task HandleBoundaryMessageFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var definition = await GetWorkflowDefinition();
        var boundaryMessage = definition.GetActivityAcrossScopes(boundaryActivityId) as MessageBoundaryEvent
            ?? throw new InvalidOperationException($"Activity '{boundaryActivityId}' is not a MessageBoundaryEvent");

        await _boundaryHandler.HandleBoundaryMessageFiredAsync(boundaryMessage, hostActivityInstanceId, definition);
        await _state.WriteStateAsync();
    }

    public async Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var definition = await GetWorkflowDefinition();
        var scopeDef = definition.GetScopeForActivity(activityId);
        var activity = scopeDef.GetActivity(activityId);

        if (activity is SignalBoundaryEvent boundarySignal)
        {
            LogSignalDeliveryBoundary(activityId);
            await _boundaryHandler.HandleBoundarySignalFiredAsync(boundarySignal, hostActivityInstanceId, definition);
        }
        else
        {
            LogSignalDeliveryComplete(activityId);
            await CompleteActivityState(activityId, new ExpandoObject());
            await ExecuteWorkflow();
        }

        await _state.WriteStateAsync();
    }

    public async Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var definition = await GetWorkflowDefinition();
        var boundarySignal = definition.GetActivityAcrossScopes(boundaryActivityId) as SignalBoundaryEvent
            ?? throw new InvalidOperationException($"Activity '{boundaryActivityId}' is not a SignalBoundaryEvent");

        await _boundaryHandler.HandleBoundarySignalFiredAsync(boundarySignal, hostActivityInstanceId, definition);
        await _state.WriteStateAsync();
    }

    private async Task RegisterMessageSubscription(Guid variablesId, string messageDefinitionId, string activityId)
    {
        var definition = await GetWorkflowDefinition();
        var messageDef = definition.Messages.First(m => m.Id == messageDefinitionId);

        if (messageDef.CorrelationKeyExpression is null)
            throw new InvalidOperationException(
                $"Message '{messageDef.Name}' has no correlationKeyExpression — cannot auto-subscribe.");

        var entry = State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException($"Active entry not found for '{activityId}'");

        var correlationValue = await GetVariable(variablesId, messageDef.CorrelationKeyExpression);
        var correlationKey = correlationValue?.ToString()
            ?? throw new InvalidOperationException(
                $"Correlation variable '{messageDef.CorrelationKeyExpression}' is null for message '{messageDef.Name}'.");

        await _state.WriteStateAsync();

        var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);

        try
        {
            await correlationGrain.Subscribe(correlationKey, this.GetPrimaryKey(), activityId, entry.ActivityInstanceId);
        }
        catch (Exception ex)
        {
            LogMessageSubscriptionFailed(activityId, messageDef.Name, correlationKey, ex);
            await FailActivityWithBoundaryCheck(activityId, ex);
            await _state.WriteStateAsync();
            return;
        }

        LogMessageSubscriptionRegistered(activityId, messageDef.Name, correlationKey);
    }

    private async Task RegisterTimerReminder(Guid hostActivityInstanceId, string timerActivityId, TimeSpan dueTime)
    {
        var callbackGrain = _grainFactory.GetGrain<ITimerCallbackGrain>(
            this.GetPrimaryKey(), $"{hostActivityInstanceId}:{timerActivityId}");
        await callbackGrain.Activate(dueTime);
        LogTimerReminderRegistered(timerActivityId, dueTime);
    }

    private async Task RegisterBoundaryMessageSubscription(Guid variablesId, Guid hostActivityInstanceId, string boundaryActivityId, string messageDefinitionId)
    {
        var definition = await GetWorkflowDefinition();
        var messageDef = definition.Messages.First(m => m.Id == messageDefinitionId);

        if (messageDef.CorrelationKeyExpression is null)
            return;

        var correlationValue = await GetVariable(variablesId, messageDef.CorrelationKeyExpression);
        if (correlationValue is null)
            return;

        var correlationKey = correlationValue.ToString()!;
        var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);

        try
        {
            await correlationGrain.Subscribe(correlationKey, this.GetPrimaryKey(), boundaryActivityId, hostActivityInstanceId);
        }
        catch (Exception ex)
        {
            LogMessageSubscriptionFailed(boundaryActivityId, messageDef.Name, correlationKey, ex);
            return;
        }

        LogMessageSubscriptionRegistered(boundaryActivityId, messageDef.Name, correlationKey);
    }

    private async Task RegisterSignalSubscription(string signalName, string activityId, Guid activityInstanceId)
    {
        await _state.WriteStateAsync();

        var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalName);

        try
        {
            await signalGrain.Subscribe(this.GetPrimaryKey(), activityId, activityInstanceId);
        }
        catch (Exception ex)
        {
            LogSignalSubscriptionFailed(activityId, signalName, ex);
            await FailActivityWithBoundaryCheck(activityId, ex);
            await _state.WriteStateAsync();
            return;
        }

        LogSignalSubscriptionRegistered(activityId, signalName);
    }

    private async Task RegisterBoundarySignalSubscription(Guid hostActivityInstanceId, string boundaryActivityId, string signalName)
    {
        var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalName);

        try
        {
            await signalGrain.Subscribe(this.GetPrimaryKey(), boundaryActivityId, hostActivityInstanceId);
        }
        catch (Exception ex)
        {
            LogSignalSubscriptionFailed(boundaryActivityId, signalName, ex);
            return;
        }

        LogSignalSubscriptionRegistered(boundaryActivityId, signalName);
    }

    private async Task ThrowSignal(string signalName)
    {
        var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalName);
        var deliveredCount = await signalGrain.BroadcastSignal();
        LogSignalThrown(signalName, deliveredCount);
    }
}
