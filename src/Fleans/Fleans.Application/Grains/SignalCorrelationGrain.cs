using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

// One bucket of a signal name (integer key = bucket, key extension = signal name); see
// ISignalCorrelationGrain. A broadcast reaches all buckets in parallel
// (SignalCorrelationGrainFactoryExtensions.BroadcastSignal).
//
// [Reentrant] is required to avoid deadlocks when a non-interrupting event
// sub-process re-subscribes during BroadcastSignal delivery (same instance → same bucket).
// A SemaphoreSlim mutex serialises all state-mutation windows (subscribe, unsubscribe,
// snapshot+clear and restore in broadcast). The mutex is released before fan-out delivery
// so reentrancy can proceed without deadlock.
[Reentrant]
[CorePlacement]
public partial class SignalCorrelationGrain : Grain, ISignalCorrelationGrain
{
    private readonly IPersistentState<SignalCorrelationState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SignalCorrelationGrain> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

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
        var signalName = SignalName;

        await _mutex.WaitAsync();
        try
        {
            if (!_state.State.AddSubscription(
                    new SignalSubscription(workflowInstanceId, activityId, hostActivityInstanceId)
                        { SignalName = signalName }))
            {
                LogDuplicateSubscription(signalName, workflowInstanceId, activityId);
                return;
            }

            await WriteStateAsync();
            LogSubscribed(signalName, workflowInstanceId, activityId);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask Unsubscribe(Guid workflowInstanceId, string activityId)
    {
        var signalName = SignalName;

        await _mutex.WaitAsync();
        try
        {
            if (_state.State.RemoveSubscription(workflowInstanceId, activityId))
            {
                await WriteStateAsync();
                LogUnsubscribed(signalName, workflowInstanceId, activityId);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private string SignalName
    {
        get
        {
            this.GetPrimaryKeyLong(out var signalName);
            return signalName!;
        }
    }

    // Storage writes only the change set the state recorded; once it has, the set is forgotten.
    // On failure the set is kept, so the next write retries it.
    private async Task WriteStateAsync()
    {
        await _state.WriteStateAsync();
        _state.State.AcceptChanges();
    }

    private const int DeliveryBatchSize = 20;
    private const int HighSubscriberCountThreshold = 50;

    public async ValueTask<int> BroadcastSignal()
    {
        var signalName = SignalName;
        List<SignalSubscription> subscribers;

        await _mutex.WaitAsync();
        try
        {
            if (_state.State.Subscriptions.Count == 0)
            {
                LogBroadcastNoSubscribers(signalName);
                return 0;
            }

            subscribers = _state.State.TakeAllSubscriptions();
            try
            {
                await WriteStateAsync();
            }
            catch
            {
                // Nothing was delivered: put the subscriptions back so memory matches storage.
                // The kept change set (clear + re-add) lands with the next successful write.
                foreach (var sub in subscribers)
                    _state.State.AddSubscription(sub);
                throw;
            }
        }
        finally
        {
            _mutex.Release();
        }

        if (subscribers.Count > HighSubscriberCountThreshold)
            LogBroadcastHighSubscriberCount(signalName, subscribers.Count, HighSubscriberCountThreshold);

        LogBroadcastStarted(signalName, subscribers.Count);

        var deliveredCount = 0;
        var toRestore = new List<SignalSubscription>();

        foreach (var batch in subscribers.Chunk(DeliveryBatchSize))
        {
            var batchTasks = batch.Select(async sub =>
            {
                try
                {
                    var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(sub.WorkflowInstanceId);
                    await workflowInstance.HandleSignalDelivery(sub.ActivityId, sub.HostActivityInstanceId);
                    return (Sub: sub, Delivered: true, Restore: false);
                }
                catch (Exception ex)
                {
                    LogDeliveryFailed(signalName, sub.WorkflowInstanceId, sub.ActivityId, ex);
                    return (Sub: sub, Delivered: false, Restore: IsTransient(ex));
                }
            });

            var results = await Task.WhenAll(batchTasks);
            deliveredCount += results.Count(r => r.Delivered);
            toRestore.AddRange(results.Where(r => r.Restore).Select(r => r.Sub));
        }

        LogBroadcastCompleted(signalName, deliveredCount, subscribers.Count);

        if (toRestore.Count > 0)
            await RestoreSubscriptions(signalName, toRestore);

        return deliveredCount;
    }

    // #742: a transient delivery failure (timeout, unreachable silo, instance reloading) must not
    // drop the subscription for good — it goes back into the bucket and receives the next
    // broadcast. A subscription re-added in the meantime (non-interrupting re-subscribe) wins:
    // AddSubscription ignores the key. Other failures (the instance is gone or rejects the
    // delivery) stay dropped, so a dead subscription is not retried on every broadcast.
    private async Task RestoreSubscriptions(string signalName, List<SignalSubscription> subscriptions)
    {
        await _mutex.WaitAsync();
        try
        {
            var restored = subscriptions.Count(sub => _state.State.AddSubscription(sub));
            if (restored == 0)
                return;

            try
            {
                await WriteStateAsync();
            }
            catch (Exception ex)
            {
                // The restored rows stay in memory and in the change set; the next write lands them.
                LogRestoreWriteFailed(signalName, restored, ex);
                return;
            }

            LogSubscriptionsRestored(signalName, restored);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static bool IsTransient(Exception ex) => ex is TimeoutException or OrleansException;

    [LoggerMessage(EventId = 9110, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' subscription registered: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogSubscribed(string signalName, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9111, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' subscription removed: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogUnsubscribed(string signalName, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9112, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' broadcast started: {SubscriberCount} subscribers")]
    private partial void LogBroadcastStarted(string signalName, int subscriberCount);

    [LoggerMessage(EventId = 9113, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' broadcast completed: {DeliveredCount}/{TotalCount} delivered")]
    private partial void LogBroadcastCompleted(string signalName, int deliveredCount, int totalCount);

    [LoggerMessage(EventId = 9114, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' broadcast — no subscribers")]
    private partial void LogBroadcastNoSubscribers(string signalName);

    [LoggerMessage(EventId = 9115, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' delivery failed to workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDeliveryFailed(string signalName, Guid workflowInstanceId, string activityId, Exception exception);

    [LoggerMessage(EventId = 9116, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' duplicate subscription ignored: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDuplicateSubscription(string signalName, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9117, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' broadcast has {SubscriberCount} subscribers (threshold: {Threshold}) — delivering in batches")]
    private partial void LogBroadcastHighSubscriberCount(string signalName, int subscriberCount, int threshold);

    [LoggerMessage(EventId = 9118, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' restored {RestoredCount} subscriptions after transient delivery failures; they receive the next broadcast")]
    private partial void LogSubscriptionsRestored(string signalName, int restoredCount);

    [LoggerMessage(EventId = 9119, Level = LogLevel.Error,
        Message = "Signal '{SignalName}' failed to persist {RestoredCount} restored subscriptions; the next write retries")]
    private partial void LogRestoreWriteFailed(string signalName, int restoredCount, Exception exception);
}
