using Fleans.Application.Abstractions.Events;
using Fleans.Application.Placement;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Projections;

/// <summary>
/// Builds the read model of one workflow instance off the instance's hot path (#861). The instance
/// grain appends to its journal and publishes <see cref="InstanceAdvanced"/>; this grain reads the
/// journal itself (latest snapshot plus tail, through <see cref="JournalReplay"/> — the replay the
/// grain uses on activation) and writes the projection up to the journal's head.
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
/// reflects and a write lands only past it, so a replay changes nothing. A failed projection is
/// rethrown for the stream to redeliver; if delivery gives up, the instance grain's activation
/// catch-up asks again.
/// </para>
/// <para>
/// <b>Placement.</b> On the <c>Fleans:Role=Projector</c> silos (the <c>fleans-projector</c>
/// deployment) when the cluster has any, on the core silos otherwise; see
/// <see cref="ProjectorPlacementAttribute"/> and <see cref="ProjectorSilo"/>.
/// </para>
/// </remarks>
[ImplicitStreamSubscription(WorkflowEventStreams.InstanceAdvancedStreamNamespace)]
[ProjectorPlacement]
public partial class WorkflowInstanceProjector : Grain, IWorkflowInstanceProjector, IAsyncObserver<InstanceAdvanced>
{
    private readonly IEventStore _eventStore;
    private readonly IWorkflowStateProjection _projection;
    private readonly ILogger<WorkflowInstanceProjector> _logger;

    /// <summary>The journal version this activation last projected; 0 when it has not projected yet.</summary>
    private int _projectedVersion;

    public WorkflowInstanceProjector(
        IEventStore eventStore,
        IWorkflowStateProjection projection,
        ILogger<WorkflowInstanceProjector> logger)
    {
        _eventStore = eventStore;
        _projection = projection;
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
        if (item.Version <= _projectedVersion)
        {
            LogNotificationCoalesced(item.InstanceId, item.Version, _projectedVersion);
            return;
        }

        try
        {
            var (state, version, _) = await JournalReplay.LoadAsync(_eventStore, item.InstanceId);
            if (version == 0)
            {
                // No journal: the instance was purged (#862) after this notification was sent.
                // Writing would resurrect an empty read-model row.
                LogJournalGone(item.InstanceId, item.Version);
                return;
            }

            await _projection.WriteAsync(item.InstanceId.ToString(), state, version);
            _projectedVersion = version;
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
