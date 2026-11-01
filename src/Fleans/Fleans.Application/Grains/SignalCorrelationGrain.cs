using Fleans.Application.Abstractions.Events;
using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

// One bucket of a signal name (integer key = bucket, key extension = signal name); see
// ISignalCorrelationGrain. A broadcast reaches all buckets in parallel
// (SignalCorrelationGrainFactoryExtensions.BroadcastSignal).
//
// A broadcast does not call the instances: the bucket takes its subscriptions and publishes them
// in SignalDeliveryPages on its own events.SignalDelivery stream, and SignalDeliveryGrain delivers
// them at a bounded pace (#976). The broadcast returns once the pages are published.
//
// [Reentrant]: the delivery grain restores failed subscriptions while a later broadcast or a
// subscribe may be running. A SemaphoreSlim mutex serialises all state-mutation windows
// (subscribe, unsubscribe, snapshot+clear and restore in broadcast).
[Reentrant]
[CorePlacement]
public partial class SignalCorrelationGrain : Grain, ISignalCorrelationGrain
{
    private readonly IPersistentState<SignalCorrelationState> _state;
    private readonly ILogger<SignalCorrelationGrain> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SignalCorrelationGrain(
        [PersistentState("state", GrainStorageNames.SignalCorrelations)]
        IPersistentState<SignalCorrelationState> state,
        ILogger<SignalCorrelationGrain> logger)
    {
        _state = state;
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

    private const int PublishPageSize = 50;

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
                // Nothing was published: put the subscriptions back so memory matches storage.
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

        // Hand the deliveries to this bucket's stream, PublishPageSize per event. One op-id per
        // subscription and broadcast: the instance applies the item of a redelivered page once.
        // A page that could not be published puts its subscriptions back for the next broadcast,
        // as a failed delivery does (#742).
        var bucket = (int)this.GetPrimaryKeyLong(out _);
        var stream = this.GetStreamProvider(WorkflowEventStreams.StreamProvider)
            .GetStream<SignalDeliveryPage>(StreamId.Create(
                WorkflowEventStreams.SignalDeliveryStreamNamespace,
                WorkflowEventStreams.SignalDeliveryStreamKey(bucket, signalName)));
        var publishedCount = 0;
        var toRestore = new List<SignalSubscription>();

        foreach (var page in subscribers.Chunk(PublishPageSize))
        {
            var items = page
                .Select(sub => new SignalDeliveryItem(
                    sub.WorkflowInstanceId, sub.ActivityId, sub.HostActivityInstanceId, $"signal:{Guid.NewGuid():N}"))
                .ToList();
            try
            {
                await stream.OnNextAsync(new SignalDeliveryPage(signalName, bucket, items));
                publishedCount += items.Count;
            }
            catch (Exception ex)
            {
                LogPagePublishFailed(signalName, items.Count, ex);
                toRestore.AddRange(page);
            }
        }

        LogBroadcastPublished(signalName, publishedCount, subscribers.Count);

        if (toRestore.Count > 0)
            await RestoreSubscriptions(signalName, toRestore);

        return publishedCount;
    }

    public async ValueTask RestoreSubscriptions(List<SignalDeliveryItem> items)
    {
        var signalName = SignalName;
        await RestoreSubscriptions(signalName, items
            .Select(i => new SignalSubscription(i.WorkflowInstanceId, i.ActivityId, i.HostActivityInstanceId) { SignalName = signalName })
            .ToList());
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



    [LoggerMessage(EventId = 9114, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' broadcast — no subscribers")]
    private partial void LogBroadcastNoSubscribers(string signalName);


    [LoggerMessage(EventId = 9116, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' duplicate subscription ignored: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDuplicateSubscription(string signalName, Guid workflowInstanceId, string activityId);


    [LoggerMessage(EventId = 9118, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' restored {RestoredCount} subscriptions after transient delivery failures; they receive the next broadcast")]
    private partial void LogSubscriptionsRestored(string signalName, int restoredCount);

    [LoggerMessage(EventId = 9119, Level = LogLevel.Error,
        Message = "Signal '{SignalName}' failed to persist {RestoredCount} restored subscriptions; the next write retries")]
    private partial void LogRestoreWriteFailed(string signalName, int restoredCount, Exception exception);


    [LoggerMessage(EventId = 9121, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' broadcast published: {PublishedCount}/{TotalCount} deliveries handed to the stream")]
    private partial void LogBroadcastPublished(string signalName, int publishedCount, int totalCount);

    [LoggerMessage(EventId = 9122, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' delivery page of {ItemCount} subscriptions could not be published; they wait for the next broadcast")]
    private partial void LogPagePublishFailed(string signalName, int itemCount, Exception exception);
}
