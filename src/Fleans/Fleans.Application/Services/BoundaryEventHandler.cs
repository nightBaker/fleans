using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Services;

public partial class BoundaryEventHandler : IBoundaryEventHandler
{
    private IBoundaryEventStateAccessor _accessor = null!;
    private ILogger _logger = null!;

    public void Initialize(IBoundaryEventStateAccessor accessor)
    {
        _accessor = accessor;
        _logger = accessor.Logger;
    }

    public async Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId, IWorkflowDefinition definition)
    {
        var attachedActivityId = boundaryTimer.AttachedToActivityId;

        // Check if attached activity is still active (lookup by instance ID)
        var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
            e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
        if (attachedEntry == null)
        {
            LogStaleBoundaryTimerIgnored(boundaryTimer.ActivityId, hostActivityInstanceId);
            return;
        }

        // Interrupt the attached activity
        var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
        await attachedInstance.Cancel($"Interrupted by boundary timer event '{boundaryTimer.ActivityId}'");
        await _accessor.CancelScopeChildren(attachedEntry.ActivityInstanceId);
        _accessor.State.CompleteEntries([attachedEntry]);

        // Timer fired, so unsubscribe message and signal boundaries
        var variablesId = await attachedInstance.GetVariablesStateId();
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, variablesId, definition);
        await UnsubscribeBoundarySignalSubscriptionsAsync(attachedActivityId, definition);
        LogBoundaryTimerInterrupted(boundaryTimer.ActivityId, attachedActivityId);

        // Create and execute boundary timer event instance
        await CreateAndExecuteBoundaryInstanceAsync(boundaryTimer, attachedInstance, definition);
    }

    public async Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId, IWorkflowDefinition definition)
    {
        var attachedActivityId = boundaryMessage.AttachedToActivityId;

        // Check if attached activity is still active (lookup by instance ID)
        var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
            e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
        if (attachedEntry == null)
        {
            LogStaleBoundaryMessageIgnored(boundaryMessage.ActivityId, hostActivityInstanceId);
            return;
        }

        // Interrupt the attached activity
        var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
        await attachedInstance.Cancel($"Interrupted by boundary message event '{boundaryMessage.ActivityId}'");
        await _accessor.CancelScopeChildren(attachedEntry.ActivityInstanceId);
        _accessor.State.CompleteEntries([attachedEntry]);

        // Clean up all boundary events for the interrupted activity
        await UnregisterBoundaryTimerRemindersAsync(attachedActivityId, attachedEntry.ActivityInstanceId, definition);
        // Unsubscribe other boundary messages, but skip the one that fired
        // (its subscription was already removed by DeliverMessage, and calling
        // back into the same correlation grain would deadlock)
        var firedMessageDef = definition.Messages.First(m => m.Id == boundaryMessage.MessageDefinitionId);
        var variablesId = await attachedInstance.GetVariablesStateId();
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, variablesId, definition, skipMessageName: firedMessageDef.Name);
        await UnsubscribeBoundarySignalSubscriptionsAsync(attachedActivityId, definition);
        LogBoundaryMessageInterrupted(boundaryMessage.ActivityId, attachedActivityId);

        // Create and execute boundary message event instance
        await CreateAndExecuteBoundaryInstanceAsync(boundaryMessage, attachedInstance, definition);
    }

    public async Task HandleBoundarySignalFiredAsync(SignalBoundaryEvent boundarySignal, Guid hostActivityInstanceId, IWorkflowDefinition definition)
    {
        var attachedActivityId = boundarySignal.AttachedToActivityId;

        var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
            e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
        if (attachedEntry == null)
        {
            LogStaleBoundarySignalIgnored(boundarySignal.ActivityId, hostActivityInstanceId);
            return;
        }

        var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
        await attachedInstance.Cancel($"Interrupted by boundary signal event '{boundarySignal.ActivityId}'");
        await _accessor.CancelScopeChildren(attachedEntry.ActivityInstanceId);
        _accessor.State.CompleteEntries([attachedEntry]);

        await UnregisterBoundaryTimerRemindersAsync(attachedActivityId, attachedEntry.ActivityInstanceId, definition);
        var variablesId = await attachedInstance.GetVariablesStateId();
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, variablesId, definition);
        var firedSignalDef = definition.Signals.First(s => s.Id == boundarySignal.SignalDefinitionId);
        await UnsubscribeBoundarySignalSubscriptionsAsync(attachedActivityId, definition, skipSignalName: firedSignalDef.Name);
        LogBoundarySignalInterrupted(boundarySignal.ActivityId, attachedActivityId);

        await CreateAndExecuteBoundaryInstanceAsync(boundarySignal, attachedInstance, definition);
    }

    public async Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid activityInstanceId, IWorkflowDefinition definition)
    {
        LogBoundaryEventTriggered(boundaryError.ActivityId, activityId);

        await _accessor.CancelScopeChildren(activityInstanceId);

        var activityGrain = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(activityInstanceId);
        await CreateAndExecuteBoundaryInstanceAsync(boundaryError, activityGrain, definition);
    }

    public async Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId, IWorkflowDefinition definition)
    {
        var scopeActivities = (definition.FindScopeForActivity(activityId) ?? definition).Activities;
        foreach (var boundaryTimer in scopeActivities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == activityId))
        {
            var callbackGrain = _accessor.GrainFactory.GetGrain<ITimerCallbackGrain>(
                _accessor.State.Id, $"{hostActivityInstanceId}:{boundaryTimer.ActivityId}");
            await callbackGrain.Cancel();
            LogTimerReminderUnregistered(boundaryTimer.ActivityId);
        }
    }

    public async Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, Guid variablesId, IWorkflowDefinition definition, string? skipMessageName = null)
    {
        var scopeActivities = (definition.FindScopeForActivity(activityId) ?? definition).Activities;
        foreach (var boundaryMsg in scopeActivities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == activityId))
        {
            var messageDef = definition.Messages.FirstOrDefault(m => m.Id == boundaryMsg.MessageDefinitionId);
            if (messageDef?.CorrelationKeyExpression is null) continue;
            if (messageDef.Name == skipMessageName) continue;

            var correlationValue = await _accessor.GetVariable(variablesId, messageDef.CorrelationKeyExpression);
            if (correlationValue is null) continue;

            var correlationGrain = _accessor.GrainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);
            await correlationGrain.Unsubscribe(correlationValue.ToString()!);
        }
    }

    public async Task UnsubscribeBoundarySignalSubscriptionsAsync(string activityId, IWorkflowDefinition definition, string? skipSignalName = null)
    {
        var scopeActivities = (definition.FindScopeForActivity(activityId) ?? definition).Activities;
        foreach (var boundarySignal in scopeActivities.OfType<SignalBoundaryEvent>()
            .Where(bs => bs.AttachedToActivityId == activityId))
        {
            var signalDef = definition.Signals.FirstOrDefault(s => s.Id == boundarySignal.SignalDefinitionId);
            if (signalDef is null) continue;
            if (signalDef.Name == skipSignalName) continue;

            var signalGrain = _accessor.GrainFactory.GetGrain<ISignalCorrelationGrain>(signalDef.Name);
            await signalGrain.Unsubscribe(_accessor.State.Id, boundarySignal.ActivityId);
        }
    }

    private async Task CreateAndExecuteBoundaryInstanceAsync(Activity boundaryActivity, IActivityInstanceGrain sourceInstance, IWorkflowDefinition definition)
    {
        var boundaryInstanceId = Guid.NewGuid();
        var boundaryInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(boundaryInstanceId);
        var variablesId = await sourceInstance.GetVariablesStateId();
        await boundaryInstance.SetActivity(boundaryActivity.ActivityId, boundaryActivity.GetType().Name);
        await boundaryInstance.SetVariablesId(variablesId);

        var boundaryEntry = new ActivityInstanceEntry(boundaryInstanceId, boundaryActivity.ActivityId, _accessor.State.Id);
        _accessor.State.AddEntries([boundaryEntry]);

        await boundaryActivity.ExecuteAsync(_accessor.WorkflowExecutionContext, boundaryInstance, definition);
        await _accessor.TransitionToNextActivity();
        await _accessor.ExecuteWorkflow();
    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning, Message = "Stale boundary timer {TimerActivityId} ignored — host activity instance {HostActivityInstanceId} already completed")]
    private partial void LogStaleBoundaryTimerIgnored(string timerActivityId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "Stale boundary message {MessageActivityId} ignored — host activity instance {HostActivityInstanceId} already completed")]
    private partial void LogStaleBoundaryMessageIgnored(string messageActivityId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Boundary error event {BoundaryEventId} triggered by failed activity {ActivityId}")]
    private partial void LogBoundaryEventTriggered(string boundaryEventId, string activityId);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Information, Message = "Timer reminder unregistered for activity {TimerActivityId}")]
    private partial void LogTimerReminderUnregistered(string timerActivityId);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Information, Message = "Boundary timer {BoundaryTimerId} interrupted attached activity {AttachedActivityId}")]
    private partial void LogBoundaryTimerInterrupted(string boundaryTimerId, string attachedActivityId);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Information, Message = "Boundary message {BoundaryMessageId} interrupted attached activity {AttachedActivityId}")]
    private partial void LogBoundaryMessageInterrupted(string boundaryMessageId, string attachedActivityId);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "Stale boundary signal {SignalActivityId} ignored — host activity instance {HostActivityInstanceId} already completed")]
    private partial void LogStaleBoundarySignalIgnored(string signalActivityId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Information, Message = "Boundary signal {BoundarySignalId} interrupted attached activity {AttachedActivityId}")]
    private partial void LogBoundarySignalInterrupted(string boundarySignalId, string attachedActivityId);
}
