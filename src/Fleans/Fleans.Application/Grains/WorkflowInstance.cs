using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Application.Adapters;
using Fleans.Application.Effects;
using Fleans.Application.Logging;
using Fleans.Application.Placement;
using Fleans.Application.QueryModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans;
using Orleans.EventSourcing;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Runtime;
using System.Collections.Concurrent;
using System.Dynamic;

namespace Fleans.Application.Grains;

// WorkflowInstance is a thin coordinator grain split into 3 partial files.
// Place new methods in the correct file:
//   * WorkflowInstance.cs               — fields, constructor, lifecycle overrides (OnActivateAsync,
//                                         OnDeactivateAsync, TransitionState), ICustomStorageInterface
//                                         (ReadStateFromStorage, ApplyUpdatesToStorage), and all public
//                                         entry points: workflow lifecycle (StartWorkflow);
//                                         activity completion/failure (CompleteActivity, FailActivity,
//                                         CompleteConditionSequence); child-workflow coordination
//                                         (OnChildWorkflowCompleted,
//                                         OnChildWorkflowFailed); external events (HandleTimerFired,
//                                         HandleMessageDelivery, HandleBoundaryMessageFired,
//                                         HandleSignalDelivery, HandleBoundarySignalFired); user tasks
//                                         (ClaimUserTask, UnclaimUserTask, CompleteUserTask); state
//                                         queries (GetState, GetVariable, GetVariables,
//                                         GetActiveActivities, GetCompletedActivities,
//                                         GetConditionSequenceStates, SetConditionSequenceResult,
//                                         FindForkByToken); request-context / scope utilities.
//   * WorkflowInstance.Infrastructure.cs — private infrastructure (RunExecutionLoop,
//                                         ResolveExternalCompletions, ComputeTransitionsForEntries,
//                                         HandleScopeCompletions, PerformEffects, PerformMessageSubscribe,
//                                         PerformSignalSubscribe, PerformStartChildWorkflow,
//                                         PublishDomainEvent, ProcessPendingEvents,
//                                         ProcessPendingEventsTimer, DisposePendingEventsTimerIfTerminal,
//                                         DeactivateIfTerminal,
//                                         EnsurePendingEventsTimerRegistered, DrainAndRaiseEvents,
//                                         LogEvent).
//   * WorkflowInstance.Logging.cs       — all [LoggerMessage] partial method declarations.
[CorePlacement]
public partial class WorkflowInstance :
    JournaledGrain<WorkflowInstanceState, WorkflowEvent>,
    IWorkflowInstanceGrain,
    ICustomStorageInterface<WorkflowInstanceState, WorkflowEvent>
{
    public ValueTask<Guid> GetWorkflowInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());
    public ValueTask<string?> GetBusinessId() => ValueTask.FromResult(State.BusinessId);
    private IWorkflowDefinition? _workflowDefinition;
    private WorkflowExecution? _execution;

    private readonly IGrainFactory _grainFactory;
    private readonly WorkflowDefinitionCache _definitions;
    private readonly ILogger<WorkflowInstance> _logger;
    private readonly IEventStore _eventStore;
    private readonly IWorkflowStateProjection _projection;
    private readonly EffectDispatcher _effectDispatcher;
    private readonly int _maxVariablesSizeBytes;

    private readonly ConcurrentQueue<(PendingExternalEvent Event, TaskCompletionSource Completion)> _pendingExternalEvents = new();
    private IGrainTimer? _pendingEventsTimer;

    /// <summary>
    /// Serializes durable enqueue writes (RaiseEvent + ConfirmEvents) from the [AlwaysInterleave]
    /// enqueue methods, which can otherwise overlap. Each enqueue persists a PendingEventEnqueued
    /// event so the queue survives forced deactivation (#657).
    /// </summary>
    private readonly SemaphoreSlim _enqueueLock = new(1, 1);

    /// <summary>
    /// Maps op-key → in-flight completion task for enqueued-but-not-yet-drained operations,
    /// so a concurrent retry with the same op-key awaits the original rather than double-enqueueing.
    /// Void ops store a <see cref="Task"/>; the escalation op stores a <see cref="Task{T}"/> of
    /// <see cref="EscalationHandledResult"/>. Cleared when the operation drains.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task> _inFlightOperations = new();

    /// <summary>Activation finished loading State; a later <see cref="ReadStateFromStorage"/> is a re-read.</summary>
    private bool _activated;

    /// <summary>
    /// A re-read after a failed or conflicting append found the journal ahead of this activation,
    /// and Orleans replaced <c>State</c> with that journal's state while the aggregate still holds
    /// the old object (#845). The activation rejects new calls until it deactivates; the next one
    /// replays the journal.
    /// </summary>
    private bool _stateReloaded;

    /// <summary>The version the stored snapshot covers; a new one is due every <see cref="IEventStore.SnapshotInterval"/> events.</summary>
    private int _snapshotVersion;

    /// <summary>The number of events in the journal, as of the last read or append.</summary>
    private int _journalVersion;

    /// <summary>
    /// The tries of the append Orleans is retrying, in order from <see cref="_unconfirmedFromVersion"/>
    /// (#877): each one's first event, its length, and the batch id it was stored with. A failed try
    /// may have been stored after all, so a retry resends it with the same id and the store confirms
    /// it instead of appending it twice. Cleared once the append succeeds or conflicts.
    /// </summary>
    private readonly List<(WorkflowEvent First, int Count, Guid BatchId)> _unconfirmedBatches = [];

    /// <summary>The version the first of <see cref="_unconfirmedBatches"/> was tried at.</summary>
    private int _unconfirmedFromVersion;

    /// <summary>
    /// The read-model row as this activation last wrote (or read) it; <c>null</c> when unknown. An
    /// append writes only when the state's row differs from it (#878).
    /// </summary>
    private WorkflowInstanceRow? _projectedRow;

    /// <summary>
    /// Serializes read-model writes: an append's write, the retry timer and the activation catch-up
    /// would otherwise interleave (an [AlwaysInterleave] enqueue confirms while a retry awaits) on
    /// the same rows.
    /// </summary>
    private readonly SemaphoreSlim _projectionLock = new(1, 1);

    /// <summary>The instance is terminal and asked to be deactivated once idle (#860).</summary>
    private bool _terminalDeactivationRequested;

    private IGrainTimer? _projectionRetryTimer;
    private int _projectionRetryAttempt;
    private static readonly TimeSpan ProjectionRetryFirstDelay = TimeSpan.FromSeconds(2);
    private const int ProjectionRetryLimit = 8; // 2 s doubling: gives up after ~8.5 minutes

    /// <summary>The main turn has applied events to State that are not drained into the journal yet.</summary>
    private bool HasUndrainedEvents => _execution is not null && _execution.GetUncommittedEvents().Count > 0;

    /// <summary>
    /// An <c>EnqueueDurable</c> (serialized by <see cref="_enqueueLock"/>) is running: it changes
    /// <c>State.PendingOperations</c> before its event is appended. The read model does not
    /// project that collection, but a snapshot taken meanwhile would be ahead of its version.
    /// </summary>
    private bool EnqueueInFlight => _enqueueLock.CurrentCount == 0;

    public WorkflowInstance(
        IGrainFactory grainFactory,
        WorkflowDefinitionCache definitions,
        ILogger<WorkflowInstance> logger,
        IEventStore eventStore,
        IWorkflowStateProjection projection,
        EffectDispatcher effectDispatcher,
        IOptions<WorkflowVariablesOptions> variablesOptions)
    {
        _grainFactory = grainFactory;
        _definitions = definitions;
        _logger = logger;
        _eventStore = eventStore;
        _projection = projection;
        _effectDispatcher = effectDispatcher;
        _maxVariablesSizeBytes = variablesOptions.Value.MaxSizeBytes;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Loads State through ReadStateFromStorage, which replays the journal itself.
        await base.OnActivateAsync(cancellationToken);
        _activated = true;

        RebuildPendingExternalEvents();
    }

    /// <summary>
    /// After journal replay, rebuild the in-memory pending-events queue from State.PendingOperations
    /// with fresh TaskCompletionSources (the originals were lost on deactivation). On retry the caller
    /// re-calls and hits the AppliedOperations short-circuit; here we just re-process the durable queue
    /// so no enqueued event is lost. (#657)
    /// </summary>
    private void RebuildPendingExternalEvents()
    {
        if (State.PendingOperations.Count == 0)
            return;

        foreach (var (opKey, payload) in State.PendingOperations)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingExternalEvent inMemory = payload switch
            {
                Fleans.Domain.Events.PendingChildCompletedPayload p =>
                    new PendingChildCompleted(p.ParentActivityId, p.ChildVariables) { OperationId = opKey },
                Fleans.Domain.Events.PendingChildFailedPayload p =>
                    new PendingChildFailed(p.ParentActivityId, new Exception(p.ExceptionMessage)) { OperationId = opKey },
                Fleans.Domain.Events.PendingSignalDeliveryPayload p =>
                    new PendingSignalDelivery(p.ActivityId, p.HostActivityInstanceId) { OperationId = opKey },
                Fleans.Domain.Events.PendingBoundarySignalFiredPayload p =>
                    new PendingBoundarySignalFired(p.BoundaryActivityId, p.HostActivityInstanceId) { OperationId = opKey },
                Fleans.Domain.Events.PendingChildEscalationRaisedPayload p =>
                    new PendingChildEscalationRaised(
                        p.ChildWorkflowInstanceId, p.HostActivityId, p.EscalationCode, p.Variables,
                        new TaskCompletionSource<EscalationHandledResult>(TaskCreationOptions.RunContinuationsAsynchronously))
                    { OperationId = opKey, EscalationInstanceId = p.EscalationInstanceId },
                _ => throw new InvalidOperationException($"Unknown pending payload type: {payload.GetType().Name}")
            };

            _inFlightOperations[opKey] = inMemory is PendingChildEscalationRaised esc
                ? esc.Tcs.Task
                : tcs.Task;
            _pendingExternalEvents.Enqueue((inMemory, tcs));
        }

        EnsurePendingEventsTimerRegistered();
    }

    // ── JournaledGrain: TransitionState ─────────────────────────────────

    /// <summary>
    /// Always a no-op. <c>State</c> is the adaptor's confirmed view, which the aggregate mutates
    /// before every <c>RaiseEvent</c>, and <see cref="ReadStateFromStorage"/> replays the journal
    /// itself — so there is nothing left to apply. Orleans calls this after each append with the
    /// events just written; applying them would apply them twice (#846).
    /// </summary>
    protected override void TransitionState(
        WorkflowInstanceState state, WorkflowEvent @event)
    {
    }

    // ── ICustomStorageInterface ─────────────────────────────────────────

    public async Task<KeyValuePair<int, WorkflowInstanceState>> ReadStateFromStorage()
    {
        var instanceId = this.GetPrimaryKey();
        if (_activated && !_stateReloaded)
        {
            // Orleans re-reads after a failed or conflicting append, swaps State for the object
            // returned here, then retries the batch on it (#845). The live State already holds the
            // batch (the aggregate applies before RaiseEvent): if nothing was stored past what this
            // activation knows, it is exactly the journal plus that retried batch — keep it.
            var stored = await _eventStore.ReadEventsAsync(instanceId, _journalVersion);
            if (stored.Count == 0)
            {
                LogAppendRetryKeepsState(_journalVersion);
                return new(_journalVersion, State);
            }

            // The failed try may have been stored after all (the error came after the commit). The
            // retry, at this same version, resends it with its batch id and the store confirms it
            // rather than appending it twice (#877). If the batch there is another writer's, that
            // retry conflicts, the tries are dropped, and the next re-read takes the path below.
            if (_unconfirmedBatches.Count > 0)
            {
                LogAppendRetryByBatchId(_journalVersion, _journalVersion + stored.Count);
                return new(_journalVersion, State);
            }

            // The journal moved on without this activation (another writer): State becomes the
            // journal's, the aggregate is out of date.
            _stateReloaded = true;
            LogStateReloaded(_journalVersion, _journalVersion + stored.Count);
            DeactivateOnIdle();
        }

        var grainId = instanceId.ToString();
        // Independent of the state reads below, so it runs alongside them.
        var projectedRowTask = ReadProjectedRow(grainId);

        // The journal is the source of truth; the snapshot only shortens replay. A failed read
        // (database error, or a snapshot from an older build — pre-1.0 needs a fresh database)
        // propagates: Orleans' CustomStorage adaptor retries ReadStateFromStorage with backoff,
        // so the activation waits rather than degrading to a full replay.
        var (snapshot, snapshotVersion) = await _eventStore.ReadSnapshotAsync(instanceId);
        var state = snapshot ?? new WorkflowInstanceState();
        var events = await _eventStore.ReadEventsAsync(instanceId, snapshotVersion);
        if (events.Count > 0)
        {
            // Apply handlers read only the event (ids and OccurredAt included), so this rebuilds
            // exactly the state the live run had.
            var replay = new WorkflowExecution(state);
            foreach (var evt in events)
                replay.ReplayEvent(evt);
        }

        var version = snapshotVersion + events.Count;
        _snapshotVersion = snapshotVersion;
        _journalVersion = version;

        // Repair: the stored row differs from the journal's state when a projection write was lost
        // (the silo stopped after an append, or the retries gave up). Unknown (read failed) counts
        // as different. Comparing content, not versions, is what lets appends skip unchanged rows.
        var (rowRead, projectedRow) = await projectedRowTask;
        _projectedRow = projectedRow;
        if (version > 0 && (!rowRead || projectedRow != WorkflowInstanceRow.From(state)))
        {
            LogProjectionBehind(version);
            if (!await TryWriteProjection(grainId, state, version))
                ScheduleProjectionRetry();
        }

        return new(version, state);
    }

    private async Task<(bool Read, WorkflowInstanceRow? Row)> ReadProjectedRow(string grainId)
    {
        try
        {
            return (true, await _projection.ReadRowAsync(grainId));
        }
        catch (Exception ex)
        {
            LogProjectionRowReadFailed(ex);
            return (false, null);
        }
    }

    /// <summary>
    /// The only way to the read model. Serialized by <see cref="_projectionLock"/>. Writes only when
    /// <paramref name="state"/>'s row differs from <see cref="_projectedRow"/> — a running instance's
    /// row changes on a handful of events, not on every append. A completed instance always writes:
    /// its details are projected too, and appends after completion are rare. Failures are logged
    /// and reported, never thrown.
    /// </summary>
    private async Task<bool> TryWriteProjection(string grainId, WorkflowInstanceState state, int version)
    {
        await _projectionLock.WaitAsync();
        try
        {
            var row = WorkflowInstanceRow.From(state);
            if (row == _projectedRow && !state.IsCompleted)
                return true;
            await _projection.WriteAsync(grainId, state);
            _projectedRow = row;
            return true;
        }
        catch (Exception ex)
        {
            LogProjectionWriteFailed(version, ex);
            return false;
        }
        finally
        {
            _projectionLock.Release();
        }
    }

    /// <summary>
    /// A failed read-model write must not wait for the next append — the failed one may be the
    /// instance's last (e.g. WorkflowCompleted). A keep-alive timer rewrites it with doubling delays;
    /// after <see cref="ProjectionRetryLimit"/> attempts it gives up (Error 1135) and lets the grain
    /// deactivate, leaving the catch-up to the next activation instead of pinning the grain and
    /// rewriting a write that keeps failing forever.
    /// </summary>
    private void ScheduleProjectionRetry()
    {
        if (_projectionRetryTimer is not null)
            return;
        _projectionRetryAttempt = 0;
        _projectionRetryTimer = this.RegisterGrainTimer(RetryProjection,
            new GrainTimerCreationOptions(ProjectionRetryFirstDelay, Timeout.InfiniteTimeSpan) { KeepAlive = true });
    }

    private async Task RetryProjection(CancellationToken cancellationToken)
    {
        // After a re-read State lags _journalVersion; the next activation catches the read model up.
        if (_stateReloaded)
        {
            StopProjectionRetry();
            return;
        }

        // State ahead of the journal: the drain's own projection write follows and settles the retry.
        if (!HasUndrainedEvents)
        {
            var version = _journalVersion;
            if (await TryWriteProjection(this.GetPrimaryKey().ToString(), State, version))
            {
                LogProjectionCaughtUp(version);
                StopProjectionRetry();
                DeactivateIfTerminal();
                return;
            }

            if (++_projectionRetryAttempt >= ProjectionRetryLimit)
            {
                LogProjectionRetryGaveUp(version, _projectionRetryAttempt);
                StopProjectionRetry();
                return;
            }
        }

        _projectionRetryTimer?.Change(ProjectionRetryFirstDelay * (1 << _projectionRetryAttempt), Timeout.InfiniteTimeSpan);
    }

    private void StopProjectionRetry()
    {
        _projectionRetryTimer?.Dispose();
        _projectionRetryTimer = null;
    }

    public async Task<bool> ApplyUpdatesToStorage(
        IReadOnlyList<WorkflowEvent> updates, int expectedVersion)
    {
        var instanceId = this.GetPrimaryKey();
        var grainId = instanceId.ToString();
        // expectedVersion is the confirmed event count BEFORE these updates.
        if (!await AppendIdempotently(instanceId, updates, expectedVersion))
            return false;

        // The append is the commit. Everything below derives from the journal and must not
        // throw: Orleans would retry a batch that is already stored and append it twice.
        var version = expectedVersion + updates.Count;
        _journalVersion = version;

        // A durable enqueue can confirm while the main turn has applied events to State but not
        // drained them; State then holds mutations the journal lacks. Skip — the drain's own
        // append writes both.
        if (HasUndrainedEvents)
            return true;

        // After a re-read, State lacks this batch (the adaptor applies nothing; see
        // TransitionState). The next activation replays it and catches the read model up.
        if (_stateReloaded)
            return true;

        if (await TryWriteProjection(grainId, State, version))
            StopProjectionRetry();
        else
            ScheduleProjectionRetry();

        // A snapshot must be exactly the state at `version`. Log writes run one at a time, so an
        // enqueue that changed State during the awaits above is still in flight here — skip, a
        // later append writes it. No await separates this check from WriteSnapshotAsync's
        // (synchronous) serialization.
        var lag = version - _snapshotVersion;
        if (!EnqueueInFlight && lag >= _eventStore.SnapshotInterval)
        {
            try
            {
                await _eventStore.WriteSnapshotAsync(instanceId, version, State);
                _snapshotVersion = version;
            }
            catch (Exception ex)
            {
                // Error, not Warning: once the tail passes Persistence:MaxEventsPerLoad, the
                // instance can no longer activate.
                LogSnapshotWriteFailed(version, lag, ex);
            }
        }

        return true;
    }

    /// <summary>
    /// Appends Orleans' batch so that a retry never stores an event twice (#877). Orleans retries a
    /// failed append with every pending event — the failed try's own event objects, followed by any
    /// raised since — at the version <see cref="ReadStateFromStorage"/> returned, which stays at the
    /// failed try's version. Each earlier try is resent as it was, with its batch id, so the store
    /// confirms one that was stored after all; the events past them form a new batch with a new id.
    /// </summary>
    private async Task<bool> AppendIdempotently(Guid instanceId, IReadOnlyList<WorkflowEvent> updates, int expectedVersion)
    {
        if (!IsRetryOfUnconfirmedBatches(updates, expectedVersion))
            _unconfirmedBatches.Clear();
        _unconfirmedFromVersion = expectedVersion;

        var tried = _unconfirmedBatches.Sum(b => b.Count);
        if (tried < updates.Count)
            _unconfirmedBatches.Add((updates[tried], updates.Count - tried, Guid.NewGuid()));

        var offset = 0;
        foreach (var batch in _unconfirmedBatches)
        {
            var events = batch.Count == updates.Count ? updates : updates.Skip(offset).Take(batch.Count).ToList();
            // A throw keeps the tries for the retry: any of them may have been stored.
            if (!await _eventStore.AppendEventsAsync(instanceId, events, expectedVersion + offset, batch.BatchId))
            {
                // Another writer's batch is there. Orleans re-reads, and the activation retires.
                _unconfirmedBatches.Clear();
                return false;
            }
            offset += batch.Count;
        }

        _unconfirmedBatches.Clear();
        return true;
    }

    /// <summary>
    /// <paramref name="updates"/> starts with the unconfirmed tries, in order, at their version. Always
    /// true for an Orleans retry: its pending queue only grows at the end until a write succeeds.
    /// </summary>
    private bool IsRetryOfUnconfirmedBatches(IReadOnlyList<WorkflowEvent> updates, int expectedVersion)
    {
        if (_unconfirmedBatches.Count == 0 || expectedVersion != _unconfirmedFromVersion)
            return false;
        var offset = 0;
        foreach (var batch in _unconfirmedBatches)
        {
            if (offset + batch.Count > updates.Count || !ReferenceEquals(updates[offset], batch.First))
                return false;
            offset += batch.Count;
        }
        return true;
    }

    private async ValueTask EnsureExecution()
    {
        ThrowIfStateReloaded();
        await EnsureWorkflowDefinitionAsync();
        _execution ??= new WorkflowExecution(State, _workflowDefinition!);
    }

    /// <summary>
    /// The one start path (#863): creates and starts the instance in a single call and a single
    /// journal append — <c>WorkflowStarted</c>, the start activity, the parent link, the input
    /// variables and <c>ExecutionStarted</c> — then runs it until it waits. A retried start of an
    /// instance already started from the same process definition is a no-op (1147), so a caller
    /// that lost the reply can call again.
    /// </summary>
    public async Task StartWorkflow(
        IWorkflowDefinition workflow,
        string? startActivityId = null,
        string? businessId = null,
        ExpandoObject? variables = null,
        ParentInfo? parent = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ThrowIfStateReloaded();
        if (State.IsStarted)
        {
            if (State.ProcessDefinitionId != workflow.ProcessDefinitionId)
                throw new InvalidOperationException(
                    $"Workflow instance '{this.GetPrimaryKey()}' is already started from '{State.ProcessDefinitionId}'.");
            LogDuplicateStartIgnored(workflow.ProcessDefinitionId);
            return;
        }
        EnsureVariablesWithinLimit(variables);

        // CreateAndStart validates (business id, start activity) before emitting anything. Assign
        // the fields only after it succeeds, so a rejected start leaves the grain unset and a retry
        // with valid input works.
        var execution = new WorkflowExecution(State, workflow);
        var scopeEntryEffects = execution.CreateAndStart(this.GetPrimaryKey(), startActivityId, businessId, variables, parent);
        _workflowDefinition = workflow;
        _execution = execution;

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowStarted();

        // Root-scope entry effects: event sub-process timer/message listener registrations.
        if (scopeEntryEffects.Count > 0)
        {
            LogRootScopeListenersArmed(scopeEntryEffects.Count);
            await PerformEffects(scopeEntryEffects);
        }

        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    private async ValueTask<IWorkflowDefinition> GetWorkflowDefinition()
    {
        await EnsureWorkflowDefinitionAsync();
        return _workflowDefinition!;
    }

    private async ValueTask EnsureWorkflowDefinitionAsync()
    {
        if (_workflowDefinition is not null)
            return;

        // No ProcessDefinitionId ⇒ SetWorkflow never ran ⇒ this grain id is not a workflow
        // instance. KeyNotFoundException maps to 404 at the API (GlobalExceptionHandler);
        // the previous InvalidOperationException surfaced as a 409 with an internal message.
        var processDefId = State.ProcessDefinitionId
            ?? throw new KeyNotFoundException($"Workflow instance '{this.GetPrimaryKey()}' not found.");

        // The silo's shared copy: no grain call and no copy per activation (#860).
        _workflowDefinition = await _definitions.GetAsync(processDefId);
    }

    /// <summary>
    /// Rejects a variables document larger than <see cref="WorkflowVariablesOptions.MaxSizeBytes"/>
    /// (measured as JSON) before the operation applies anything. Called first by every entry point
    /// that takes variables from outside the engine.
    /// </summary>
    private void EnsureVariablesWithinLimit(ExpandoObject? variables)
    {
        if (IsWithinVariablesLimit(variables, out var size))
            return;

        throw new ArgumentException(
            $"Variables are {size} bytes as JSON; the limit is {_maxVariablesSizeBytes} " +
            $"({WorkflowVariablesOptions.SectionName}:MaxSizeBytes).");
    }

    private bool IsWithinVariablesLimit(ExpandoObject? variables, out int size)
    {
        size = variables is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(variables));
        if (size <= _maxVariablesSizeBytes)
            return true;

        LogVariablesTooLarge(size, _maxVariablesSizeBytes);
        return false;
    }

    private void SetWorkflowRequestContext()
    {
        if (_workflowDefinition is null) return;

        WorkflowLoggingContext.SetInstance(
            _workflowDefinition.WorkflowId, _workflowDefinition.ProcessDefinitionId, this.GetPrimaryKey(), State.BusinessId);
    }

    private IDisposable? BeginWorkflowScope()
    {
        if (_workflowDefinition is null) return null;

        return WorkflowLoggingContext.BeginScope(
            _logger, _workflowDefinition.WorkflowId, _workflowDefinition.ProcessDefinitionId, this.GetPrimaryKey(),
            State.BusinessId);
    }

    // ── Activity Lifecycle ──────────────────────────────────────────────

    public async Task CompleteActivity(string activityId, ExpandoObject variables)
    {
        EnsureVariablesWithinLimit(variables);
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompletingActivity(activityId);

        var effects = _execution!.CompleteActivity(activityId, null, variables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public async Task CompleteActivity(string activityId, Guid activityInstanceId, ExpandoObject variables)
    {
        EnsureVariablesWithinLimit(variables);
        await EnsureExecution();

        // Stale callback guard
        if (!State.HasActiveEntry(activityInstanceId))
        {
            LogStaleCallbackIgnored(activityId, activityInstanceId, "CompleteActivity");
            return;
        }

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompletingActivity(activityId);

        var effects = _execution!.CompleteActivity(activityId, activityInstanceId, variables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public async Task FailActivity(string activityId, Exception exception)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogFailingActivity(activityId);

        var effects = _execution!.FailActivity(activityId, null, exception);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public Task FailActivity(string activityId, Guid activityInstanceId, Exception exception) =>
        FailActivity(activityId, activityInstanceId, exception, variables: null);

    public async Task FailActivity(string activityId, Guid activityInstanceId, Exception exception, ExpandoObject? variables)
    {
        // The failure must land even when its variables are too large — rejecting it would leave
        // the activity running forever — so oversized failure variables are dropped (1140).
        if (!IsWithinVariablesLimit(variables, out _))
            variables = null;
        await EnsureExecution();

        // Stale callback guard
        if (!State.HasActiveEntry(activityInstanceId))
        {
            LogStaleCallbackIgnored(activityId, activityInstanceId, "FailActivity");
            return;
        }

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogFailingActivity(activityId);

        var effects = _execution!.FailActivity(activityId, activityInstanceId, exception, variables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public async Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogConditionResult(conditionSequenceId, result);

        _execution!.CompleteConditionSequence(activityId, conditionSequenceId, result);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public async Task CompleteActivationCondition(string activityId, Guid activityInstanceId, bool result)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompleteActivationCondition(activityId, activityInstanceId, result);

        var joinState = State.GetComplexGatewayJoinState(activityId);
        if (joinState is null || joinState.HasFired)
        {
            LogComplexGatewayActivationConditionLateCallback(activityInstanceId);
            return;
        }
        if (!result)
        {
            LogComplexGatewayWaitingForMoreTokens(
                activityInstanceId, joinState.WaitingTokenCount, joinState.ActivationCondition);
            return;
        }
        _execution!.MarkComplexGatewayJoinFired(activityId);
        LogComplexGatewayActivationConditionMet(
            joinState.ActivationCondition, activityInstanceId, joinState.WaitingTokenCount);

        _execution!.CompleteComplexGatewayJoin(activityId);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public async Task OnChildWorkflowCompleted(string operationId, string parentActivityId, ExpandoObject childVariables)
    {
        LogChildWorkflowCompletedQueued(parentActivityId);

        // (a) Already applied → caller is retrying; short-circuit to the persisted (void) outcome.
        if (State.TryGetAppliedOperation(operationId, out _))
        {
            LogPendingOpDeduplicated(operationId);
            return;
        }

        // (b) In-flight → return the existing TCS task (no double-enqueue).
        if (TryGetInFlight(operationId, out var inflight))
        {
            await inflight;
            return;
        }

        // (c) Fresh → durably persist + enqueue.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueDurable(operationId,
            new PendingChildCompletedPayload(parentActivityId, childVariables),
            new PendingChildCompleted(parentActivityId, childVariables) { OperationId = operationId },
            tcs);
        await tcs.Task;
    }

    public async Task OnChildWorkflowFailed(string operationId, string parentActivityId, Exception exception)
    {
        LogChildWorkflowFailedQueued(parentActivityId);

        if (State.TryGetAppliedOperation(operationId, out _))
        {
            LogPendingOpDeduplicated(operationId);
            return;
        }

        if (TryGetInFlight(operationId, out var inflight))
        {
            await inflight;
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueDurable(operationId,
            new PendingChildFailedPayload(parentActivityId, exception.Message),
            new PendingChildFailed(parentActivityId, exception) { OperationId = operationId },
            tcs);
        await tcs.Task;
    }

    public async Task<EscalationHandledResult> OnChildEscalationRaised(
        string operationId, Guid escalationInstanceId,
        Guid childWorkflowInstanceId, string hostActivityId,
        string escalationCode, ExpandoObject variables)
    {
        LogChildEscalationRaisedQueued(childWorkflowInstanceId, escalationCode);

        // (a) Already applied → return the persisted EscalationHandledResult without re-processing.
        if (State.TryGetAppliedOperation(operationId, out var applied))
        {
            LogPendingOpDeduplicated(operationId);
            return applied?.EscalationResult ?? EscalationHandledResult.Unhandled;
        }

        // (b) In-flight → await the existing in-memory TCS so the caller observes the same result.
        if (TryGetInFlightEscalation(operationId, out var inflight))
        {
            return await inflight;
        }

        // (c) Fresh → durably persist + enqueue.
        var resultTcs = new TaskCompletionSource<EscalationHandledResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueDurable(operationId,
            new PendingChildEscalationRaisedPayload(
                childWorkflowInstanceId, hostActivityId, escalationCode, variables, escalationInstanceId),
            new PendingChildEscalationRaised(
                childWorkflowInstanceId, hostActivityId, escalationCode, variables, resultTcs)
            { OperationId = operationId, EscalationInstanceId = escalationInstanceId },
            wrapper);
        return await resultTcs.Task;
    }

    // ── Event Handling ──────────────────────────────────────────────────

    public async Task<TimeSpan?> HandleTimerFired(string timerActivityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogTimerReminderFired(timerActivityId);

        // Pre-call stale guard: catches late fires (post-completion, post-cancel,
        // post-host-scope-completion) before aggregate dispatch. Pairs with
        // TimerCallbackGrain.Cancel's swallow-on-failure cleanup per the
        // registration-vs-cleanup asymmetry rule (CLAUDE.md Design constraints).
        // Mirrors the aggregate's internal guards in HandleTimerFired and
        // TryActivateTimerEventSubProcess — see WorkflowExecution.IsTimerFireStale.
        if (_execution!.IsTimerFireStale(timerActivityId, hostActivityInstanceId))
        {
            LogTimerFiredAfterActivityResolved(timerActivityId, hostActivityInstanceId);
            return null;
        }

        var effects = _execution.HandleTimerFired(timerActivityId, hostActivityInstanceId);

        // Intercept RegisterTimerEffect for cycle re-registration: return the DueTime
        // instead of calling callbackGrain.Activate() — avoids Orleans non-reentrant
        // grain deadlock when TimerCallbackGrain calls back to itself (issue #130).
        TimeSpan? cycleReRegistration = null;
        var filteredEffects = new List<IInfrastructureEffect>();
        foreach (var effect in effects)
        {
            if (effect is RegisterTimerEffect timerEffect
                && timerEffect.TimerActivityId == timerActivityId
                && timerEffect.HostActivityInstanceId == hostActivityInstanceId)
            {
                cycleReRegistration = timerEffect.DueTime;
                LogTimerCycleReRegistrationDeferred(timerActivityId, timerEffect.DueTime);
            }
            else
            {
                filteredEffects.Add(effect);
            }
        }

        await PerformEffects(filteredEffects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
        return cycleReRegistration;
    }

    public async Task HandleMessageDelivery(string activityId, Guid hostActivityInstanceId, ExpandoObject variables)
    {
        EnsureVariablesWithinLimit(variables);
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var effects = _execution!.HandleMessageDelivery(activityId, hostActivityInstanceId, variables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public async Task HandleBoundaryMessageFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        // HandleMessageDelivery on the aggregate handles both boundary and non-boundary
        var effects = _execution!.HandleMessageDelivery(boundaryActivityId, hostActivityInstanceId, new ExpandoObject());
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public async Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId)
    {
        LogSignalDeliveryQueued(activityId, hostActivityInstanceId);
        // Signal path is never retried (subscription is consumed once), so a fresh-Guid op-key is
        // generated at enqueue time and persisted in the raised event — never regenerated on replay (#657).
        var opKey = $"signal:{Guid.NewGuid()}";
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueDurable(opKey,
            new PendingSignalDeliveryPayload(activityId, hostActivityInstanceId),
            new PendingSignalDelivery(activityId, hostActivityInstanceId) { OperationId = opKey },
            tcs);
        await tcs.Task;
    }

    public async Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        LogBoundarySignalFiredQueued(boundaryActivityId, hostActivityInstanceId);
        var opKey = $"boundary-signal:{Guid.NewGuid()}";
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueDurable(opKey,
            new PendingBoundarySignalFiredPayload(boundaryActivityId, hostActivityInstanceId),
            new PendingBoundarySignalFired(boundaryActivityId, hostActivityInstanceId) { OperationId = opKey },
            tcs);
        await tcs.Task;
    }

    // ── User Task Lifecycle ──────────────────────────────────────────────

    public async Task ClaimUserTask(Guid activityInstanceId, string userId, IReadOnlyList<string> userGroups)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogUserTaskClaimAttempt(activityInstanceId, userId);

        try
        {
            var effects = _execution!.ClaimUserTask(activityInstanceId, userId, userGroups);
            await PerformEffects(effects);
            await ProcessPendingEvents();
        }
        catch (InvalidOperationException)
        {
            LogUserTaskClaimRejected(activityInstanceId, userId, userGroups.Count);
            throw;
        }
    }

    public async Task UnclaimUserTask(Guid activityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogUserTaskUnclaimAttempt(activityInstanceId);

        var effects = _execution!.UnclaimUserTask(activityInstanceId);
        await PerformEffects(effects);
        await ProcessPendingEvents();
    }

    public async Task CompleteUserTask(Guid activityInstanceId, string userId, ExpandoObject variables)
    {
        EnsureVariablesWithinLimit(variables);
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogUserTaskCompleteAttempt(activityInstanceId, userId);

        var effects = _execution!.CompleteUserTask(activityInstanceId, userId, variables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public async Task FailUserTask(Guid activityInstanceId, string errorCode, string errorMessage)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogUserTaskFailAttempt(activityInstanceId, errorCode);

        var effects = _execution!.FailUserTask(activityInstanceId, errorCode, errorMessage);
        await PerformEffects(effects);
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }

    public async Task CancelUserTask(Guid activityInstanceId, string? reason)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogUserTaskCancelAttempt(activityInstanceId, reason);

        var effects = _execution!.CancelUserTask(activityInstanceId, reason);
        await PerformEffects(effects);
        await ProcessPendingEvents();
    }

    public async Task CompleteMultiInstanceEarly(string hostActivityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogMultiInstanceEarlyCompletion(hostActivityInstanceId, hostActivityId);

        var effects = _execution!.CompleteMultiInstanceEarly(hostActivityId, hostActivityInstanceId);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        await ProcessPendingEvents();
    }


    // ── State Facade ────────────────────────────────────────────────────

    public ValueTask<object?> GetVariable(Guid variablesId, string variableName)
        => ValueTask.FromResult(State.GetVariable(variablesId, variableName));

    public ValueTask<ExpandoObject> GetVariables(Guid variablesStateId)
        => ValueTask.FromResult(State.GetMergedVariables(variablesStateId));

    public ValueTask<ExpandoObject?> GetVariablesIfActive(Guid activityInstanceId, Guid variablesStateId)
        => ValueTask.FromResult(State.HasActiveEntry(activityInstanceId)
            ? State.GetMergedVariables(variablesStateId)
            : null);

    public ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities()
    {
        IReadOnlyList<IActivityExecutionContext> result = State.GetActiveActivities()
            .Select(e => (IActivityExecutionContext)new ActivityExecutionContextAdapter(e))
            .ToList().AsReadOnly();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities()
    {
        IReadOnlyList<IActivityExecutionContext> result = State.GetCompletedActivities()
            .Select(e => (IActivityExecutionContext)new ActivityExecutionContextAdapter(e))
            .ToList().AsReadOnly();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates()
    {
        IReadOnlyDictionary<Guid, ConditionSequenceState[]> result = State.ConditionSequenceStates
            .GroupBy(c => c.GatewayActivityInstanceId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        return ValueTask.FromResult(result);
    }

    // In-silo reads for the test-only WorkflowInstanceInspector grain extension, so test probes
    // stay off the remotely callable grain interface. They return copies: no caller gets a
    // handle on the event-sourced state.
    internal IReadOnlyDictionary<Guid, TransactionOutcomeRecord> CopyTransactionOutcomes()
        => new Dictionary<Guid, TransactionOutcomeRecord>(State.TransactionOutcomes);

    internal IReadOnlyList<string> CopyPendingOperationKeys() => State.PendingOperations.Keys.ToList();

    internal IReadOnlyList<string> CopyAppliedOperationKeys() => State.AppliedOperations.Keys.ToList();

    public async ValueTask<InstanceStateSnapshot> GetStateSnapshot()
    {
        // The definition only fills in condition text; the snapshot is served without it, as the
        // read model does for a completed instance.
        try
        {
            await EnsureWorkflowDefinitionAsync();
        }
        catch (Exception ex)
        {
            LogStateSnapshotDefinitionUnavailable(ex);
        }
        return InstanceStateSnapshots.From(State, _workflowDefinition);
    }

    public ValueTask<IReadOnlyList<CompensationLogEntrySnapshot>> GetCompensationLog()
    {
        IReadOnlyList<CompensationLogEntrySnapshot> result = State.CompensationLog
            .OrderBy(e => e.CompletedAtSequence)
            .Select(ToCompensationSnapshot)
            .ToList()
            .AsReadOnly();
        LogCompensationLogReturned(result.Count);
        return ValueTask.FromResult(result);
    }

    private CompensationLogEntrySnapshot ToCompensationSnapshot(CompletedActivitySnapshot entry)
    {
        // Handler resolution: walk the cached BPMN definition to the enclosing scope of the
        // compensable activity, then read the boundary event's pre-resolved HandlerActivityId.
        // Returns null when the model has drifted or no compensation boundary exists.
        var scopeDef = _workflowDefinition?.FindScopeForActivity(entry.ActivityDefinitionId);
        var handlerId = scopeDef?.FindCompensationBoundary(entry.ActivityDefinitionId)?.HandlerActivityId;

        var variables = ((IDictionary<string, object?>)entry.VariablesSnapshot)
            .ToDictionary(kvp => kvp.Key, kvp => VariableValueFormatter.Format(kvp.Value));

        return new CompensationLogEntrySnapshot(
            entry.ActivityInstanceId,
            entry.ActivityDefinitionId,
            handlerId,
            entry.CompletedAtSequence,
            entry.ScopeId,
            entry.IsCompensated,
            variables);
    }

    public async ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        await EnsureExecution();
        _execution!.EvaluateConditionSequence(activityInstanceId, sequenceId, result);
        await ProcessPendingEvents();
    }

    public ValueTask<GatewayForkState?> FindForkByToken(Guid tokenId)
        => ValueTask.FromResult(State.FindForkByToken(tokenId));

    public ValueTask<ComplexGatewayJoinState?> GetComplexGatewayJoinState(string gatewayActivityId)
        => ValueTask.FromResult(State.GetComplexGatewayJoinState(gatewayActivityId));

    private sealed class WorkflowInstanceEffectContext : IEffectContext
    {
        private readonly WorkflowInstance _grain;

        public WorkflowInstanceEffectContext(WorkflowInstance grain) => _grain = grain;

        public IGrainFactory GrainFactory => _grain._grainFactory;

        public Guid WorkflowInstanceId => _grain.GetPrimaryKey();

        public Task PersistStateAsync() => _grain.DrainAndRaiseEvents();

        public Task EnsureInstancePersistedAsync() =>
            _grain._journalVersion == 0 ? _grain.DrainAndRaiseEvents() : Task.CompletedTask;

        public async Task ProcessFailureEffects(
            string activityId, Guid hostActivityInstanceId, Exception ex)
        {
            var failEffects = _grain._execution!.FailActivity(activityId, hostActivityInstanceId, ex);
            await _grain._effectDispatcher.DispatchAsync(failEffects, this);
        }

        public EscalationHandledResult? EscalationParentResult { get; private set; }

        public void SetEscalationParentResult(EscalationHandledResult result)
            => EscalationParentResult = result;
    }
}
