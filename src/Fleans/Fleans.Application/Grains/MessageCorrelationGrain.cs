using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

// [Reentrant] is required so that Subscribe/Unsubscribe can reenter during
// DeliverMessage (the workflow grain's HandleMessageDelivery call chain may
// emit SubscribeMessageEffect targeting this same grain). A SemaphoreSlim
// mutex serialises all state-mutation windows; the mutex is released before
// awaiting external grains so reentrancy can proceed without deadlock. A
// 3-state machine (Empty → Subscribed → Delivering) with a pending-intent
// slot buffers Subscribe/Unsubscribe calls that arrive mid-delivery.
[Reentrant]
[CorePlacement]
public partial class MessageCorrelationGrain : Grain, IMessageCorrelationGrain
{
    private readonly IPersistentState<MessageCorrelationState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<MessageCorrelationGrain> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

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

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var key = this.GetPrimaryKeyString();

        if (_state.State.Status == MessageSubscriptionStatus.Delivering)
        {
            _state.State.Status = MessageSubscriptionStatus.Subscribed;
            _state.State.Pending = null;
            await _state.WriteStateAsync();
            LogCrashRecovery(key);
        }
        else if (_state.State.Subscription is not null
                 && _state.State.Status == MessageSubscriptionStatus.Empty)
        {
            _state.State.Status = MessageSubscriptionStatus.Subscribed;
        }
    }

    public async ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId)
    {
        var grainKey = this.GetPrimaryKeyString();

        await _mutex.WaitAsync();
        try
        {
            switch (_state.State.Status)
            {
                case MessageSubscriptionStatus.Empty:
                    _state.State.Subscription = new MessageSubscription(
                        workflowInstanceId, activityId, hostActivityInstanceId, grainKey)
                        { MessageName = grainKey };
                    _state.State.Status = MessageSubscriptionStatus.Subscribed;
                    await _state.WriteStateAsync();
                    LogSubscribed(grainKey, workflowInstanceId, activityId);
                    break;

                case MessageSubscriptionStatus.Subscribed:
                    throw new InvalidOperationException(
                        $"Duplicate subscription: grain '{grainKey}' already has a subscriber.");

                case MessageSubscriptionStatus.Delivering:
                    var pendingSub = new MessageSubscription(
                        workflowInstanceId, activityId, hostActivityInstanceId, grainKey)
                        { MessageName = grainKey };
                    _state.State.Pending = new PendingMessageIntent(pendingSub);
                    await _state.WriteStateAsync();
                    LogSubscribeBuffered(grainKey);
                    break;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask Unsubscribe()
    {
        var grainKey = this.GetPrimaryKeyString();

        await _mutex.WaitAsync();
        try
        {
            switch (_state.State.Status)
            {
                case MessageSubscriptionStatus.Empty:
                    break;

                case MessageSubscriptionStatus.Subscribed:
                    _state.State.Subscription = null;
                    _state.State.Status = MessageSubscriptionStatus.Empty;
                    await _state.ClearStateAsync();
                    LogUnsubscribed(grainKey);
                    break;

                case MessageSubscriptionStatus.Delivering:
                    _state.State.Pending = null;
                    await _state.WriteStateAsync();
                    LogUnsubscribeBuffered(grainKey);
                    break;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<bool> DeliverMessage(ExpandoObject variables)
    {
        var grainKey = this.GetPrimaryKeyString();
        MessageSubscription subscription;

        await _mutex.WaitAsync();
        try
        {
            switch (_state.State.Status)
            {
                case MessageSubscriptionStatus.Empty:
                    LogDeliveryNoMatch(grainKey);
                    return false;

                case MessageSubscriptionStatus.Subscribed:
                    subscription = _state.State.Subscription!;
                    _state.State.Status = MessageSubscriptionStatus.Delivering;
                    break;

                case MessageSubscriptionStatus.Delivering:
                    throw new InvalidOperationException(
                        $"Concurrent delivery: grain '{grainKey}' is already delivering.");

                default:
                    throw new InvalidOperationException($"Unexpected status: {_state.State.Status}");
            }
        }
        finally
        {
            _mutex.Release();
        }

        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(subscription.WorkflowInstanceId);
        LogDelivery(grainKey, subscription.WorkflowInstanceId, subscription.ActivityId);

        try
        {
            await workflowInstance.HandleMessageDelivery(
                subscription.ActivityId, subscription.HostActivityInstanceId, variables);
        }
        catch (Exception)
        {
            await _mutex.WaitAsync();
            try
            {
                _state.State.Status = MessageSubscriptionStatus.Subscribed;
                _state.State.Pending = null;
                await _state.WriteStateAsync();
                LogDeliveryFailedRestored(grainKey);
            }
            finally
            {
                _mutex.Release();
            }
            throw;
        }

        await _mutex.WaitAsync();
        try
        {
            if (_state.State.Pending is { } pending)
            {
                _state.State.Subscription = pending.Subscription;
                _state.State.Status = MessageSubscriptionStatus.Subscribed;
                _state.State.Pending = null;
                await _state.WriteStateAsync();
                LogPendingMaterialized(grainKey);
            }
            else
            {
                _state.State.Subscription = null;
                _state.State.Status = MessageSubscriptionStatus.Empty;
                await _state.ClearStateAsync();
            }
        }
        finally
        {
            _mutex.Release();
        }

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

    [LoggerMessage(EventId = 9005, Level = LogLevel.Debug,
        Message = "Message correlation '{GrainKey}': subscribe buffered as pending (delivering)")]
    private partial void LogSubscribeBuffered(string grainKey);

    [LoggerMessage(EventId = 9006, Level = LogLevel.Debug,
        Message = "Message correlation '{GrainKey}': unsubscribe buffered (cleared pending, delivering)")]
    private partial void LogUnsubscribeBuffered(string grainKey);

    [LoggerMessage(EventId = 9007, Level = LogLevel.Debug,
        Message = "Message correlation '{GrainKey}': pending intent materialized after delivery")]
    private partial void LogPendingMaterialized(string grainKey);

    [LoggerMessage(EventId = 9008, Level = LogLevel.Warning,
        Message = "Message correlation '{GrainKey}': delivery failed, restored Subscribed, pending dropped")]
    private partial void LogDeliveryFailedRestored(string grainKey);

    [LoggerMessage(EventId = 9009, Level = LogLevel.Warning,
        Message = "Message correlation '{GrainKey}': crash recovery — reverted Delivering to Subscribed")]
    private partial void LogCrashRecovery(string grainKey);
}
