using Fleans.Application.Abstractions.Events;
using Fleans.Application.Observability;
using Fleans.Application.Placement;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Projections;

/// <summary>
/// Builds the read model of one workflow instance off the instance's hot path (#861). The instance
/// grain appends to its journal and publishes <see cref="InstanceAdvanced"/>; this grain reads the
/// journal itself (latest snapshot plus tail, through <see cref="JournalReplay"/> — the replay the
/// grain uses on activation) and writes the projection up to the journal's head, with the silo's
/// next batch (<see cref="ProjectionBatcher"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Coalescing.</b> The stream delivers one notification at a time. Each projection goes to the
/// head, so the notifications that queued up meanwhile are at or below <see cref="_projectedVersion"/>
/// and are skipped: ten notifications in a burst cost one or two projections.
/// </para>
/// <para>
/// <b>At-least-once.</b> A redelivered notification is skipped the same way. After a reactivation
/// (no <see cref="_projectedVersion"/>), the write itself is guarded: the row stores the version it
/// reflects and a write lands only past it, so a replay changes nothing. The notification is
/// acknowledged only once the batch carrying its write is durable; a failed batch fails every
/// projection in it. A failed projection is
/// rethrown for the stream to redeliver; if delivery gives up, the instance grain's activation
/// catch-up asks again.
/// </para>
/// <para>
/// <b>Lifetime.</b> A completed instance's projector deactivates once it has written the final
/// row. A running one deactivates after <see cref="ProjectorOptions.IdleDeactivation"/> without a
/// notification (#1011): a parked instance is not notified, so its projector would otherwise stay
/// for Orleans' 15-minute collection age — one activation per waiting instance on the worker. A
/// notification after that reactivates it; with no <see cref="_projectedVersion"/> it projects to the
/// head once, behind the row's version guard.
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
    private readonly TimeSpan _idleDeactivation;

    /// <summary>Deactivates the projector once it goes <see cref="_idleDeactivation"/> without a notification.</summary>
    private IGrainTimer? _idleTimer;

    /// <summary>The journal version this activation last projected; 0 when it has not projected yet.</summary>
    private int _projectedVersion;

    public WorkflowInstanceProjector(
        IEventStore eventStore,
        ProjectionBatcher batcher,
        ProjectionReplaySlots replaySlots,
        IOptions<ProjectorOptions> options,
        ILogger<WorkflowInstanceProjector> logger)
    {
        _eventStore = eventStore;
        _batcher = batcher;
        _replaySlots = replaySlots;
        _logger = logger;
        _idleDeactivation = options.Value.IdleDeactivation;
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
        RestartIdleTimer();

        if (item.Version <= _projectedVersion)
        {
            LogNotificationCoalesced(item.InstanceId, item.Version, _projectedVersion);
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
            _projectedVersion = version;
            // One sample per write, from the notification that triggered it; the ones it
            // coalesces were published later, so their lag is no larger.
            FleansDiagnostics.RecordProjectionLag(item.PublishedAt, DateTimeOffset.UtcNow);
            LogProjected(item.InstanceId, item.Version, version);

            // Appends after completion are rare; free the activation instead of waiting for
            // idle collection. A later notification reactivates it.
            if (state.IsCompleted)
                DeactivateOnIdle();
        }
        catch (Exception ex)
        {
            LogProjectionFailed(ex, item.InstanceId, item.Version);
            throw; // the stream redelivers; the write is idempotent
        }
    }

    private void RestartIdleTimer()
    {
        if (_idleTimer is null)
            // KeepAlive = false: the timer itself never holds the activation.
            _idleTimer = this.RegisterGrainTimer(DeactivateWhenIdle,
                new GrainTimerCreationOptions(_idleDeactivation, Timeout.InfiniteTimeSpan) { KeepAlive = false });
        else
            _idleTimer.Change(_idleDeactivation, Timeout.InfiniteTimeSpan);
    }

    private Task DeactivateWhenIdle(CancellationToken cancellationToken)
    {
        LogIdleDeactivation(this.GetPrimaryKeyString(), _projectedVersion);
        DeactivateOnIdle();
        return Task.CompletedTask;
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

    [LoggerMessage(EventId = 4077, Level = LogLevel.Debug,
        Message = "Projector of instance {InstanceId} idle at version {ProjectedVersion}; deactivating")]
    private partial void LogIdleDeactivation(string instanceId, int projectedVersion);
}
