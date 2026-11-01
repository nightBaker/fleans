using Fleans.Application.Abstractions.Events;
using Fleans.Application.Placement;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Grains;

/// <summary>
/// Delivers the <see cref="SignalDeliveryPage"/>s one signal bucket published on its stream (#976),
/// so a broadcast to a large bucket does not wait for every instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded load.</b> One activation per bucket stream, not reentrant: it delivers one page at a
/// time, <see cref="SignalCorrelationOptions.DeliveryConcurrency"/> instances at a time. A broadcast
/// to every bucket keeps at most <c>BucketCount × DeliveryConcurrency</c> deliveries in flight,
/// however many subscribers it has. A delivery per instance stream was unbounded: the pulling
/// agents called thousands of instances at once, exhausted the journal's connections and stalled
/// every other workflow.
/// </para>
/// <para>
/// <b>Failures.</b> A transient failure (timeout, unreachable silo, instance reloading) puts the
/// subscription back into the bucket for the next broadcast (#742), so one unreachable instance
/// does not hold the bucket's later pages. Any other failure (the instance rejects the delivery)
/// is logged and dropped, so a dead subscription is not retried on every broadcast. If the page cannot be finished — the
/// restore fails, or the silo dies mid-page — the stream redelivers the whole page; the bucket fixed
/// each item's op-id when it took the subscription, and the instance applies an item once.
/// </para>
/// <para>
/// <b>Placement.</b> On the core silos, next to the buckets and the instances it calls.
/// </para>
/// </remarks>
[ImplicitStreamSubscription(WorkflowEventStreams.SignalDeliveryStreamNamespace)]
[CorePlacement]
public partial class SignalDeliveryGrain : Grain, ISignalDeliveryGrain, IAsyncObserver<SignalDeliveryPage>
{
    private readonly IGrainFactory _grainFactory;
    private readonly int _concurrency;
    private readonly ILogger<SignalDeliveryGrain> _logger;

    public SignalDeliveryGrain(
        IGrainFactory grainFactory, IOptions<SignalCorrelationOptions> options, ILogger<SignalDeliveryGrain> logger)
    {
        _grainFactory = grainFactory;
        _concurrency = options.Value.DeliveryConcurrency;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventStreams.StreamProvider);
        // Stream key matches the grain's primary key — see streaming.md "Subscriber-side stream-id trap".
        var streamId = StreamId.Create(WorkflowEventStreams.SignalDeliveryStreamNamespace, this.GetPrimaryKeyString());
        var stream = streamProvider.GetStream<SignalDeliveryPage>(streamId);

        foreach (var handle in await stream.GetAllSubscriptionHandles())
            await handle.ResumeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task OnNextAsync(SignalDeliveryPage page, StreamSequenceToken? token = null)
    {
        var delivered = 0;
        var toRestore = new List<SignalDeliveryItem>();

        foreach (var batch in page.Items.Chunk(_concurrency))
        {
            var results = await Task.WhenAll(batch.Select(async item =>
            {
                try
                {
                    var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(item.WorkflowInstanceId);
                    await workflowInstance.HandleSignalDelivery(item.OperationId, item.ActivityId, item.HostActivityInstanceId);
                    return (Item: item, Delivered: true, Restore: false);
                }
                catch (Exception ex)
                {
                    LogDeliveryFailed(page.SignalName, item.WorkflowInstanceId, item.ActivityId, ex);
                    return (Item: item, Delivered: false, Restore: SignalCorrelationGrain.IsTransient(ex));
                }
            }));

            delivered += results.Count(r => r.Delivered);
            toRestore.AddRange(results.Where(r => r.Restore).Select(r => r.Item));
        }

        if (toRestore.Count > 0)
        {
            // Throws when the bucket cannot be reached: the stream redelivers the page, and the
            // items already delivered are deduplicated by their op-id.
            await _grainFactory.GetGrain<ISignalCorrelationGrain>(page.Bucket, page.SignalName)
                .RestoreSubscriptions(toRestore);
        }

        LogPageDelivered(page.SignalName, page.Bucket, delivered, page.Items.Count, toRestore.Count);
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception ex)
    {
        LogStreamError(ex);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 9123, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' page of bucket {Bucket} delivered: {DeliveredCount}/{ItemCount}, {RestoredCount} put back for the next broadcast")]
    private partial void LogPageDelivered(string signalName, int bucket, int deliveredCount, int itemCount, int restoredCount);

    [LoggerMessage(EventId = 9124, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' streamed delivery failed to workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDeliveryFailed(string signalName, Guid workflowInstanceId, string activityId, Exception exception);

    [LoggerMessage(EventId = 9125, Level = LogLevel.Error, Message = "SignalDelivery stream error")]
    private partial void LogStreamError(Exception exception);
}
