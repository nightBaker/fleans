using Fleans.Application.Abstractions.Events;
using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
//
// A bucket with more subscribers than SignalCorrelationOptions.StreamDeliveryThreshold does not
// call its instances: it publishes one SignalDeliveryRequested per subscriber on the instance's
// events.SignalDelivery stream, and SignalDeliveryGrain delivers it (#976).
[Reentrant]
[CorePlacement]
public partial class SignalCorrelationGrain : Grain, ISignalCorrelationGrain
{
    private readonly IPersistentState<SignalCorrelationState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly SignalCorrelationOptions _options;
    private readonly ILogger<SignalCorrelationGrain> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SignalCorrelationGrain(
        [PersistentState("state", GrainStorageNames.SignalCorrelations)]
        IPersistentState<SignalCorrelationState> state,
        IGrainFactory grainFactory,
        IOptions<SignalCorrelationOptions> options,
        ILogger<SignalCorrelationGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _options = options.Value;
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
    private const int PublishPageSize = 200;
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

        // One op-id per subscription and broadcast: the instance applies a redelivery once (#976).
        var deliveries = subscribers.Select(sub => (Sub: sub, OperationId: $"signal:{Guid.NewGuid():N}")).ToList();

        if (subscribers.Count > _options.StreamDeliveryThreshold)
            return await PublishDeliveries(signalName, deliveries);

        if (subscribers.Count > HighSubscriberCountThreshold)
            LogBroadcastHighSubscriberCount(signalName, subscribers.Count, HighSubscriberCountThreshold);

        LogBroadcastStarted(signalName, subscribers.Count);

        var deliveredCount = 0;
        var toRestore = new List<SignalSubscription>();

        foreach (var batch in deliveries.Chunk(DeliveryBatchSize))
        {
            var batchTasks = batch.Select(async delivery =>
            {
                var sub = delivery.Sub;
                try
                {
                    var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(sub.WorkflowInstanceId);
                    await workflowInstance.HandleSignalDelivery(delivery.OperationId, sub.ActivityId, sub.HostActivityInstanceId);
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

    // #976: hand every delivery to the stream of its instance, a page at a time. The broadcast
    // returns once they are published; SignalDeliveryGrain calls the instance, and the stream
    // retries a delivery that failed transiently. A delivery that could not be published puts its
    // subscription back for the next broadcast, as a failed direct delivery does (#742).
    private async Task<int> PublishDeliveries(
        string signalName, List<(SignalSubscription Sub, string OperationId)> deliveries)
    {
        LogBroadcastStreamed(signalName, deliveries.Count, _options.StreamDeliveryThreshold);

        var streamProvider = this.GetStreamProvider(WorkflowEventStreams.StreamProvider);
        var publishedCount = 0;
        var toRestore = new List<SignalSubscription>();

        foreach (var page in deliveries.Chunk(PublishPageSize))
        {
            var pageTasks = page.Select(async delivery =>
            {
                var sub = delivery.Sub;
                try
                {
                    var stream = streamProvider.GetStream<SignalDeliveryRequested>(StreamId.Create(
                        WorkflowEventStreams.SignalDeliveryStreamNamespace, sub.WorkflowInstanceId.ToString("D")));
                    await stream.OnNextAsync(new SignalDeliveryRequested(
                        signalName, sub.WorkflowInstanceId, sub.ActivityId, sub.HostActivityInstanceId, delivery.OperationId));
                    return (Sub: sub, Published: true);
                }
                catch (Exception ex)
                {
                    LogDeliveryPublishFailed(signalName, sub.WorkflowInstanceId, sub.ActivityId, ex);
                    return (Sub: sub, Published: false);
                }
            });

            var results = await Task.WhenAll(pageTasks);
            publishedCount += results.Count(r => r.Published);
            toRestore.AddRange(results.Where(r => !r.Published).Select(r => r.Sub));
        }

        LogBroadcastPublished(signalName, publishedCount, deliveries.Count);

        if (toRestore.Count > 0)
            await RestoreSubscriptions(signalName, toRestore);

        return publishedCount;
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

    internal static bool IsTransient(Exception ex) => ex is TimeoutException or OrleansException;

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

    [LoggerMessage(EventId = 9120, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' broadcast has {SubscriberCount} subscribers (stream threshold: {Threshold}) — publishing the deliveries to the signal delivery stream")]
    private partial void LogBroadcastStreamed(string signalName, int subscriberCount, int threshold);

    [LoggerMessage(EventId = 9121, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' broadcast published: {PublishedCount}/{TotalCount} deliveries handed to the stream")]
    private partial void LogBroadcastPublished(string signalName, int publishedCount, int totalCount);

    [LoggerMessage(EventId = 9122, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' delivery to workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId} could not be published; the subscription waits for the next broadcast")]
    private partial void LogDeliveryPublishFailed(string signalName, Guid workflowInstanceId, string activityId, Exception exception);
}
