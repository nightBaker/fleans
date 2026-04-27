using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

// [Reentrant] is required to prevent deadlocks when a non-interrupting message event
// sub-process re-subscribes during DeliverMessage. The re-subscription (SubscribeMessageEffect)
// calls back into this grain while DeliverMessage is still executing (awaiting the
// delivery tcs). Without reentrancy, Subscribe() queues behind the active DeliverMessage
// activation, causing a circular wait with neither being able to complete.
[Reentrant]
[CorePlacement]
public partial class MessageCorrelationGrain : Grain, IMessageCorrelationGrain
{
    private readonly IPersistentState<MessageCorrelationState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<MessageCorrelationGrain> _logger;

    public MessageCorrelationGrain(
        [PersistentState("state", GrainStorageNames.MessageCorrelations)]
        IPersistentState<MessageCorrelationState> state,
        IGrainFactory grainFactory,
        ILogger<MessageCorrelationGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId)
    {
        var grainKey = this.GetPrimaryKeyString();

        if (_state.State.Subscription is not null)
            throw new InvalidOperationException(
                $"Duplicate subscription: grain '{grainKey}' already has a subscriber.");

        _state.State.Subscription = new MessageSubscription(workflowInstanceId, activityId, hostActivityInstanceId, grainKey)
            { MessageName = grainKey };
        await _state.WriteStateAsync();
        LogSubscribed(grainKey, workflowInstanceId, activityId);
    }

    public async ValueTask Unsubscribe()
    {
        var grainKey = this.GetPrimaryKeyString();

        if (_state.State.Subscription is not null)
        {
            _state.State.Subscription = null;
            await _state.ClearStateAsync();
            LogUnsubscribed(grainKey);
        }
    }

    public async ValueTask<bool> DeliverMessage(ExpandoObject variables)
    {
        var grainKey = this.GetPrimaryKeyString();

        if (_state.State.Subscription is null)
        {
            LogDeliveryNoMatch(grainKey);
            return false;
        }

        var subscription = _state.State.Subscription;
        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(subscription.WorkflowInstanceId);
        LogDelivery(grainKey, subscription.WorkflowInstanceId, subscription.ActivityId);

        // Deliver first, then clear — confirm-then-remove for at-least-once
        await workflowInstance.HandleMessageDelivery(subscription.ActivityId, subscription.HostActivityInstanceId, variables);

        _state.State.Subscription = null;
        await _state.ClearStateAsync();

        return true;
    }

    [LoggerMessage(EventId = 9000, Level = LogLevel.Information,
        Message = "Message correlation '{GrainKey}' subscription registered: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogSubscribed(string grainKey, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information,
        Message = "Message correlation '{GrainKey}' subscription removed")]
    private partial void LogUnsubscribed(string grainKey);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information,
        Message = "Message correlation '{GrainKey}' delivered: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDelivery(string grainKey, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Debug,
        Message = "Message correlation '{GrainKey}' delivery failed: no active subscription")]
    private partial void LogDeliveryNoMatch(string grainKey);
}
