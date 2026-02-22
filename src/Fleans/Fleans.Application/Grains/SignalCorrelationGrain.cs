using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class SignalCorrelationGrain : Grain, ISignalCorrelationGrain
{
    private readonly IPersistentState<SignalCorrelationState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SignalCorrelationGrain> _logger;

    public SignalCorrelationGrain(
        [PersistentState("state", GrainStorageNames.SignalCorrelations)]
        IPersistentState<SignalCorrelationState> state,
        IGrainFactory grainFactory,
        ILogger<SignalCorrelationGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId)
    {
        var signalName = this.GetPrimaryKeyString();

        if (_state.State.Subscriptions.Any(s =>
            s.WorkflowInstanceId == workflowInstanceId && s.ActivityId == activityId))
        {
            LogDuplicateSubscription(signalName, workflowInstanceId, activityId);
            return;
        }

        _state.State.Subscriptions.Add(new SignalSubscription(workflowInstanceId, activityId, hostActivityInstanceId)
            { SignalName = signalName });
        await _state.WriteStateAsync();
        LogSubscribed(signalName, workflowInstanceId, activityId);
    }

    public async ValueTask Unsubscribe(Guid workflowInstanceId, string activityId)
    {
        var signalName = this.GetPrimaryKeyString();
        var removed = _state.State.Subscriptions.RemoveAll(s =>
            s.WorkflowInstanceId == workflowInstanceId && s.ActivityId == activityId);

        if (removed > 0)
        {
            await _state.WriteStateAsync();
            LogUnsubscribed(signalName, workflowInstanceId, activityId);
        }
    }

    public async ValueTask<int> BroadcastSignal()
    {
        var signalName = this.GetPrimaryKeyString();

        if (_state.State.Subscriptions.Count == 0)
        {
            LogBroadcastNoSubscribers(signalName);
            return 0;
        }

        var subscribers = _state.State.Subscriptions.ToList();
        _state.State.Subscriptions.Clear();
        await _state.WriteStateAsync();

        LogBroadcastStarted(signalName, subscribers.Count);

        var deliveryTasks = subscribers.Select(async sub =>
        {
            try
            {
                var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(sub.WorkflowInstanceId);
                await workflowInstance.HandleSignalDelivery(sub.ActivityId, sub.HostActivityInstanceId);
                return true;
            }
            catch (Exception ex)
            {
                LogDeliveryFailed(signalName, sub.WorkflowInstanceId, sub.ActivityId, ex);
                return false;
            }
        });

        var results = await Task.WhenAll(deliveryTasks);
        var deliveredCount = results.Count(r => r);
        LogBroadcastCompleted(signalName, deliveredCount, subscribers.Count);

        return deliveredCount;
    }

    [LoggerMessage(EventId = 9100, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' subscription registered: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogSubscribed(string signalName, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' subscription removed: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogUnsubscribed(string signalName, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' broadcast started: {SubscriberCount} subscribers")]
    private partial void LogBroadcastStarted(string signalName, int subscriberCount);

    [LoggerMessage(EventId = 9103, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' broadcast completed: {DeliveredCount}/{TotalCount} delivered")]
    private partial void LogBroadcastCompleted(string signalName, int deliveredCount, int totalCount);

    [LoggerMessage(EventId = 9104, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' broadcast — no subscribers")]
    private partial void LogBroadcastNoSubscribers(string signalName);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' delivery failed to workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDeliveryFailed(string signalName, Guid workflowInstanceId, string activityId, Exception exception);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' duplicate subscription ignored: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDuplicateSubscription(string signalName, Guid workflowInstanceId, string activityId);
}
