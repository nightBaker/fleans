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

    public async Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId)
    {
        var attachedActivityId = boundaryTimer.AttachedToActivityId;

        // Check if attached activity is still active (lookup by instance ID)
        var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
            e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
        if (attachedEntry == null)
            return; // Activity already completed, timer is stale

        // Interrupt the attached activity
        var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
        await attachedInstance.Complete();
        _accessor.State.CompleteEntries([attachedEntry]);

        // Timer fired, so only unsubscribe message boundaries
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId);
        LogBoundaryTimerInterrupted(boundaryTimer.ActivityId, attachedActivityId);

        // Create and execute boundary timer event instance
        await CreateAndExecuteBoundaryInstanceAsync(boundaryTimer, attachedInstance);
    }

    public async Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId)
    {
        var definition = await _accessor.GetWorkflowDefinition();
        var attachedActivityId = boundaryMessage.AttachedToActivityId;

        // Check if attached activity is still active (lookup by instance ID)
        var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
            e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
        if (attachedEntry == null)
            return; // Activity already completed, message is stale

        // Interrupt the attached activity
        var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
        await attachedInstance.Complete();
        _accessor.State.CompleteEntries([attachedEntry]);

        // Clean up all boundary events for the interrupted activity
        await UnregisterBoundaryTimerRemindersAsync(attachedActivityId, attachedEntry.ActivityInstanceId);
        // Unsubscribe other boundary messages, but skip the one that fired
        // (its subscription was already removed by DeliverMessage, and calling
        // back into the same correlation grain would deadlock)
        var firedMessageDef = definition.Messages.First(m => m.Id == boundaryMessage.MessageDefinitionId);
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, skipMessageName: firedMessageDef.Name);
        LogBoundaryMessageInterrupted(boundaryMessage.ActivityId, attachedActivityId);

        // Create and execute boundary message event instance
        await CreateAndExecuteBoundaryInstanceAsync(boundaryMessage, attachedInstance);
    }

    public async Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid activityInstanceId)
    {
        LogBoundaryEventTriggered(boundaryError.ActivityId, activityId);

        var activityGrain = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(activityInstanceId);
        await CreateAndExecuteBoundaryInstanceAsync(boundaryError, activityGrain);
    }

    public async Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId)
    {
        var definition = await _accessor.GetWorkflowDefinition();

        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == activityId))
        {
            var callbackGrain = _accessor.GrainFactory.GetGrain<ITimerCallbackGrain>(
                _accessor.State.Id, $"{hostActivityInstanceId}:{boundaryTimer.ActivityId}");
            await callbackGrain.Cancel();
            LogTimerReminderUnregistered(boundaryTimer.ActivityId);
        }
    }

    public async Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, string? skipMessageName = null)
    {
        var definition = await _accessor.GetWorkflowDefinition();

        foreach (var boundaryMsg in definition.Activities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == activityId))
        {
            var messageDef = definition.Messages.FirstOrDefault(m => m.Id == boundaryMsg.MessageDefinitionId);
            if (messageDef?.CorrelationKeyExpression is null) continue;
            if (messageDef.Name == skipMessageName) continue;

            var correlationValue = await _accessor.GetVariable(messageDef.CorrelationKeyExpression);
            if (correlationValue is null) continue;

            var correlationGrain = _accessor.GrainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);
            await correlationGrain.Unsubscribe(correlationValue.ToString()!);
        }
    }

    private async Task CreateAndExecuteBoundaryInstanceAsync(Activity boundaryActivity, IActivityInstanceGrain sourceInstance)
    {
        var boundaryInstanceId = Guid.NewGuid();
        var boundaryInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(boundaryInstanceId);
        var variablesId = await sourceInstance.GetVariablesStateId();
        await boundaryInstance.SetActivity(boundaryActivity.ActivityId, boundaryActivity.GetType().Name);
        await boundaryInstance.SetVariablesId(variablesId);

        var boundaryEntry = new ActivityInstanceEntry(boundaryInstanceId, boundaryActivity.ActivityId, _accessor.State.Id);
        _accessor.State.AddEntries([boundaryEntry]);

        await boundaryActivity.ExecuteAsync(_accessor.WorkflowExecutionContext, boundaryInstance);
        await _accessor.TransitionToNextActivity();
        await _accessor.ExecuteWorkflow();
    }

    [LoggerMessage(EventId = 1016, Level = LogLevel.Information, Message = "Boundary error event {BoundaryEventId} triggered by failed activity {ActivityId}")]
    private partial void LogBoundaryEventTriggered(string boundaryEventId, string activityId);

    [LoggerMessage(EventId = 1019, Level = LogLevel.Information, Message = "Timer reminder unregistered for activity {TimerActivityId}")]
    private partial void LogTimerReminderUnregistered(string timerActivityId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Boundary timer {BoundaryTimerId} interrupted attached activity {AttachedActivityId}")]
    private partial void LogBoundaryTimerInterrupted(string boundaryTimerId, string attachedActivityId);

    [LoggerMessage(EventId = 1022, Level = LogLevel.Information, Message = "Boundary message {BoundaryMessageId} interrupted attached activity {AttachedActivityId}")]
    private partial void LogBoundaryMessageInterrupted(string boundaryMessageId, string attachedActivityId);
}
