using Fleans.Application.Abstractions.Events;
using Fleans.Application.Observability;
using Fleans.Application.Placement;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Projections;

/// <summary>
/// Builds the read model of the workflow instances of one bucket off their hot path (#861, #1011).
/// An instance grain appends to its journal and publishes <see cref="InstanceAdvanced"/> on its
/// bucket's stream (<see cref="ProjectorOptions.BucketFor"/>); this grain reads the
/// journal itself (latest snapshot plus tail, through <see cref="JournalReplay"/> — the replay the
/// grain uses on activation) and writes the projection up to the journal's head, with the silo's
/// next batch (<see cref="ProjectionBatcher"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Buckets (#1011).</b> One projector serves every instance whose id hashes to its bucket, so the
/// cluster runs <see cref="ProjectorOptions.BucketCount"/> projectors however many instances there
/// are: a burst of completions (a signal to 100 000 waiting instances) is queued on the streams of
/// the active buckets instead of placing one projector per instance, and a waiting instance holds no
/// activation. The price is that a slow projection holds back the rest of its bucket.
/// </para>
/// <para>
/// <b>Coalescing.</b> The stream delivers one notification at a time. Each projection goes to the
/// head, so the notifications of the same instance that queued up meanwhile are at or below its
/// entry in <see cref="_projectedVersions"/> and are skipped: ten notifications in a burst cost one
/// or two projections. The map keeps the last <see cref="TrackedInstances"/> instances projected;
/// an evicted one is projected again on its next notification, behind the row's version guard.
/// </para>
/// <para>
/// <b>At-least-once.</b> A redelivered notification is skipped the same way. After a reactivation
/// or an eviction (no entry in <see cref="_projectedVersions"/>), the write itself is guarded: the row stores the version it
/// reflects and a write lands only past it, so a replay changes nothing. The notification is
/// acknowledged only once the batch carrying its write is durable; a failed batch fails every
/// projection in it. A failed projection is
/// rethrown for the stream to redeliver; if delivery gives up, the instance grain's activation
/// catch-up asks again.
/// </para>
/// <para>
/// <b>Lifetime.</b> A bucket projector is notified as long as its instances advance, and Orleans
/// collects it when they all wait; it keeps nothing that a later activation cannot rebuild.
/// </para>
/// <para>
/// <b>Journal reads are limited per silo</b> (<see cref="ProjectionReplaySlots"/>, #1011): a burst
/// of notifications waits for a slot instead of exhausting the journal connection pool.
/// </para>
/// <para>
/// <b>Placement.</b> On the worker silos (<c>worker-*</c> / <c>combined-*</c>), next to the script
/// executor: projection is journal reads and read-model writes, so it is kept off the core silos
/// that run the workflows, and scales with the worker deployment. See
/// <see cref="WorkerPlacementAttribute"/>.
/// </para>
/// </remarks>
[ImplicitStreamSubscription(WorkflowEventStreams.InstanceAdvancedStreamNamespace)]
[WorkerPlacement]
public partial class WorkflowInstanceProjector : Grain, IWorkflowInstanceProjector, IAsyncObserver<InstanceAdvanced>
{
    private readonly IEventStore _eventStore;
    private readonly ProjectionBatcher _batcher;
    private readonly ProjectionReplaySlots _replaySlots;
    private readonly ILogger<WorkflowInstanceProjector> _logger;

    /// <summary>How many instances <see cref="_projectedVersions"/> remembers; the oldest leaves first.</summary>
    internal const int TrackedInstances = 1024;

    /// <summary>The journal version this activation last projected, per instance of the bucket.</summary>
    private readonly Dictionary<Guid, int> _projectedVersions = new();

    /// <summary>The instances of <see cref="_projectedVersions"/> in the order they were added.</summary>
    private readonly Queue<Guid> _trackingOrder = new();

    public WorkflowInstanceProjector(
        IEventStore eventStore,
        ProjectionBatcher batcher,
        ProjectionReplaySlots replaySlots,
        ILogger<WorkflowInstanceProjector> logger)
    {
        _eventStore = eventStore;
        _batcher = batcher;
        _replaySlots = replaySlots;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventStreams.StreamProvider);
        // Stream key matches the grain's primary key — see streaming.md "Subscriber-side stream-id trap".
        var streamId = StreamId.Create(WorkflowEventStreams.InstanceAdvancedStreamNamespace, this.GetPrimaryKeyString());
        var stream = streamProvider.GetStream<InstanceAdvanced>(streamId);

        foreach (var handle in await stream.GetAllSubscriptionHandles())
            await handle.ResumeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task OnNextAsync(InstanceAdvanced item, StreamSequenceToken? token = null)
    {
        if (_projectedVersions.TryGetValue(item.InstanceId, out var projectedVersion) && item.Version <= projectedVersion)
        {
            LogNotificationCoalesced(item.InstanceId, item.Version, projectedVersion);
            return;
        }

        try
        {
            WorkflowInstanceState state;
            int version;
            // The journal read holds one of the silo's replay slots; the write below does not, so
            // batches keep filling while the next reads run (#1011).
            using (await _replaySlots.EnterAsync())
            {
                (state, version, _) = await JournalReplay.LoadAsync(_eventStore, item.InstanceId, item.Version);
                if (version == 0)
                {
                    // No journal: the instance was purged (#862) after this notification was sent.
                    // Writing would resurrect an empty read-model row.
                    LogJournalGone(item.InstanceId, item.Version);
                    return;
                }

                // A completed instance's details are written once, here: they need the history its
                // snapshot no longer holds (#1050). A running one projects only its row.
                if (state.IsCompleted && state.HasPrunedHistory)
                    (state, version) = await JournalReplay.LoadFullHistoryAsync(_eventStore, item.InstanceId);
            }

            await _batcher.WriteAsync(item.InstanceId.ToString(), state, version);
            TrackProjected(item.InstanceId, version);
            // One sample per write, from the notification that triggered it; the ones it
            // coalesces were published later, so their lag is no larger.
            FleansDiagnostics.RecordProjectionLag(item.PublishedAt, DateTimeOffset.UtcNow);
            LogProjected(item.InstanceId, item.Version, version);
        }
        catch (Exception ex)
        {
            LogProjectionFailed(ex, item.InstanceId, item.Version);
            throw; // the stream redelivers; the write is idempotent
        }
    }

    private void TrackProjected(Guid instanceId, int version)
    {
        if (_projectedVersions.TryAdd(instanceId, version))
        {
            _trackingOrder.Enqueue(instanceId);
            if (_trackingOrder.Count > TrackedInstances)
                _projectedVersions.Remove(_trackingOrder.Dequeue());
        }
        else
        {
            _projectedVersions[instanceId] = version;
        }
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception ex)
    {
        LogStreamError(ex);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 4070, Level = LogLevel.Debug,
        Message = "Read model of instance {InstanceId} projected to version {Version} (notified of {NotifiedVersion})")]
    private partial void LogProjected(Guid instanceId, int notifiedVersion, int version);

    [LoggerMessage(EventId = 4071, Level = LogLevel.Debug,
        Message = "InstanceAdvanced {NotifiedVersion} of instance {InstanceId} skipped: already projected to version {ProjectedVersion}")]
    private partial void LogNotificationCoalesced(Guid instanceId, int notifiedVersion, int projectedVersion);

    [LoggerMessage(EventId = 4072, Level = LogLevel.Warning,
        Message = "Projecting the read model of instance {InstanceId} for version {NotifiedVersion} failed; the stream redelivers")]
    private partial void LogProjectionFailed(Exception exception, Guid instanceId, int notifiedVersion);

    [LoggerMessage(EventId = 4073, Level = LogLevel.Error, Message = "InstanceAdvanced stream error")]
    private partial void LogStreamError(Exception exception);

    [LoggerMessage(EventId = 4074, Level = LogLevel.Information,
        Message = "InstanceAdvanced {NotifiedVersion} of instance {InstanceId} skipped: its journal is gone (purged)")]
    private partial void LogJournalGone(Guid instanceId, int notifiedVersion);
}
