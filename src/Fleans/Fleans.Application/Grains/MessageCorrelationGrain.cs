using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

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

    public async ValueTask Subscribe(string correlationKey, Guid workflowInstanceId, string activityId)
    {
        var messageName = this.GetPrimaryKeyString();

        if (_state.State.Subscriptions.ContainsKey(correlationKey))
            throw new InvalidOperationException(
                $"Duplicate subscription: message '{messageName}' with correlationKey '{correlationKey}' already has a subscriber.");

        _state.State.Subscriptions[correlationKey] = new MessageSubscription(workflowInstanceId, activityId);
        await _state.WriteStateAsync();
        LogSubscribed(messageName, correlationKey, workflowInstanceId, activityId);
    }

    public async ValueTask Unsubscribe(string correlationKey)
    {
        var messageName = this.GetPrimaryKeyString();

        if (_state.State.Subscriptions.Remove(correlationKey))
        {
            await _state.WriteStateAsync();
            LogUnsubscribed(messageName, correlationKey);
        }
    }

    public async ValueTask<bool> DeliverMessage(string correlationKey, ExpandoObject variables)
    {
        var messageName = this.GetPrimaryKeyString();

        if (!_state.State.Subscriptions.TryGetValue(correlationKey, out var subscription))
        {
            LogDeliveryNoMatch(messageName, correlationKey);
            return false;
        }

        // Remove subscription before delivering (at-most-once)
        _state.State.Subscriptions.Remove(correlationKey);
        await _state.WriteStateAsync();

        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(subscription.WorkflowInstanceId);
        var definition = await workflowInstance.GetWorkflowDefinition();
        var activity = definition.GetActivity(subscription.ActivityId);

        if (activity is MessageBoundaryEvent)
        {
            LogDeliveryBoundary(messageName, correlationKey, subscription.WorkflowInstanceId, subscription.ActivityId);
            await workflowInstance.HandleBoundaryMessageFired(subscription.ActivityId);
        }
        else
        {
            LogDelivery(messageName, correlationKey, subscription.WorkflowInstanceId, subscription.ActivityId);
            await workflowInstance.CompleteActivity(subscription.ActivityId, variables);
        }

        return true;
    }

    [LoggerMessage(EventId = 9000, Level = LogLevel.Information,
        Message = "Message '{MessageName}' subscription registered: correlationKey='{CorrelationKey}', workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogSubscribed(string messageName, string correlationKey, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information,
        Message = "Message '{MessageName}' subscription removed: correlationKey='{CorrelationKey}'")]
    private partial void LogUnsubscribed(string messageName, string correlationKey);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information,
        Message = "Message '{MessageName}' delivered: correlationKey='{CorrelationKey}' -> workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDelivery(string messageName, string correlationKey, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Information,
        Message = "Message '{MessageName}' delivered as boundary: correlationKey='{CorrelationKey}' -> workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDeliveryBoundary(string messageName, string correlationKey, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Debug,
        Message = "Message '{MessageName}' delivery failed: no subscription for correlationKey='{CorrelationKey}'")]
    private partial void LogDeliveryNoMatch(string messageName, string correlationKey);
}
