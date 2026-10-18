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
//                                         entry points: workflow lifecycle (SetWorkflow, StartWorkflow);
//                                         activity completion/failure (CompleteActivity, FailActivity,
//                                         CompleteConditionSequence); child-workflow coordination
//                                         (SetParentInfo, SetInitialVariables, OnChildWorkflowCompleted,
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

    /// <summary>
    /// Guards against double-apply: true while draining aggregate events via RaiseEvent.
    /// When true, TransitionState skips event application (aggregate already applied).
    /// </summary>
    private bool _draining;

    /// <summary>
    /// Temporary aggregate used during JournaledGrain activation replay.
    /// TransitionState uses this to apply events when not in drain mode.
    /// </summary>
    private WorkflowExecution? _replayAggregate;

    /// <summary>The version the stored snapshot covers; a new one is due every <see cref="IEventStore.SnapshotInterval"/> events.</summary>
    private int _snapshotVersion;

    /// <summary>The number of events in the journal, as of the last read or append.</summary>
    private int _journalVersion;

    /// <summary>The highest version this activation has written to (or read from) the read model.</summary>
    private int _projectedVersion;

    /// <summary>
    /// Serializes read-model writes: an append's write, the retry timer and the activation catch-up
    /// would otherwise interleave (an [AlwaysInterleave] enqueue confirms while a retry awaits) on
    /// the same rows.
    /// </summary>
    private readonly SemaphoreSlim _projectionLock = new(1, 1);

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
        ILogger<WorkflowInstance> logger,
        IEventStore eventStore,
        IWorkflowStateProjection projection,
        EffectDispatcher effectDispatcher,
        IOptions<WorkflowVariablesOptions> variablesOptions)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _eventStore = eventStore;
        _projection = projection;
        _effectDispatcher = effectDispatcher;
        _maxVariablesSizeBytes = variablesOptions.Value.MaxSizeBytes;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        // _replayAggregate lifecycle: base.OnActivateAsync() calls ReadStateFromStorage()
        // then replays events via TransitionState(), which creates _replayAggregate on first call.
        // After base returns, we clear it — the real _execution aggregate will be created
        // by EnsureExecution() with the full workflow definition.
        _replayAggregate = null;

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

    protected override void TransitionState(
        WorkflowInstanceState state, WorkflowEvent @event)
    {
        if (!_draining)
        {
            // During activation replay: apply events via a temporary aggregate
            _replayAggregate ??= new WorkflowExecution(state);
            _replayAggregate.ReplayEvent(@event);
        }
        // During normal operation (_draining == true): no-op.
        // The aggregate has already applied the event before RaiseEvent was called.
    }

    // ── ICustomStorageInterface ─────────────────────────────────────────

    public async Task<KeyValuePair<int, WorkflowInstanceState>> ReadStateFromStorage()
    {
        var instanceId = this.GetPrimaryKey();
        var grainId = instanceId.ToString();
        // Independent of the state reads below, so it runs alongside them.
        var projectedVersionTask = ReadProjectedVersion(grainId);

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

        // Catch-up: the read model is behind the journal when a projection write was lost (the
        // silo stopped after an append, or the retries gave up). Unknown (read failed) counts as behind.
        var projectedVersion = await projectedVersionTask;
        _projectedVersion = projectedVersion ?? 0;
        if (version > 0 && (projectedVersion is null || projectedVersion < version))
        {
            LogProjectionBehind(projectedVersion ?? -1, version);
            if (!await TryWriteProjection(grainId, state, version, rebuild: true))
                ScheduleProjectionRetry();
        }

        return new(version, state);
    }

    private async Task<int?> ReadProjectedVersion(string grainId)
    {
        try
        {
            return await _projection.ReadVersionAsync(grainId);
        }
        catch (Exception ex)
        {
            LogProjectionVersionReadFailed(ex);
            return null;
        }
    }

    /// <summary>
    /// The only way to the read model. Serialized by <see cref="_projectionLock"/>, and never moves
    /// the projected version backwards. Failures are logged and reported, never thrown.
    /// </summary>
    private async Task<bool> TryWriteProjection(string grainId, WorkflowInstanceState state, int version, bool rebuild)
    {
        await _projectionLock.WaitAsync();
        try
        {
            if (version < _projectedVersion)
                return true; // a later write already covered this one
            await _projection.WriteAsync(grainId, state, version, rebuild);
            _projectedVersion = version;
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
        // State ahead of the journal: the drain's own projection write follows and settles the retry.
        if (!HasUndrainedEvents)
        {
            var version = _journalVersion;
            if (await TryWriteProjection(this.GetPrimaryKey().ToString(), State, version, rebuild: true))
            {
                LogProjectionCaughtUp(version);
                StopProjectionRetry();
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
        var success = await _eventStore.AppendEventsAsync(instanceId, updates, expectedVersion);
        if (!success) return false;

        // The append is the commit. Everything below derives from the journal and must not
        // throw: Orleans would retry a batch that is already stored and append it twice.
        var version = expectedVersion + updates.Count;
        _journalVersion = version;

        // A durable enqueue can confirm while the main turn has applied events to State but not
        // drained them; State then holds mutations the journal lacks. Skip — the drain's own
        // append writes both.
        if (HasUndrainedEvents)
            return true;

        if (await TryWriteProjection(grainId, State, version, rebuild: false))
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

    private async ValueTask EnsureExecution()
    {
        await EnsureWorkflowDefinitionAsync();
        _execution ??= new WorkflowExecution(State, _workflowDefinition!);
    }

    public async Task SetWorkflow(IWorkflowDefinition workflow, string? startActivityId = null, string? businessId = null)
    {
        if (_workflowDefinition is not null) throw new ArgumentException("Workflow already set");
        ArgumentNullException.ThrowIfNull(workflow);

        // Start validates (business id, start activity) before emitting anything. Assign the
        // fields only after it succeeds, so a rejected start leaves the grain unset and a retry
        // with valid input works. (A storage failure in ProcessPendingEvents is not covered.)
        var execution = new WorkflowExecution(State, workflow);
        execution.Start(startActivityId, businessId, this.GetPrimaryKey());
        _workflowDefinition = workflow;
        _execution = execution;

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowDefinitionSet();

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

        var key = ProcessDefinition.ExtractKeyFromId(processDefId);
        var grain = _grainFactory.GetGrain<IProcessDefinitionGrain>(key);
        _workflowDefinition = await grain.GetDefinitionById(processDefId);
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

    // ── StartWorkflow (from Execution) ──────────────────────────────────

    public async Task StartWorkflow()
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        // MarkExecutionStarted emits ExecutionStarted AND returns the root-scope
        // entry effects (event sub-process timer/message listener registrations).
        // The grain never reaches into the aggregate to assemble these itself.
        var scopeEntryEffects = _execution!.MarkExecutionStarted();
        LogWorkflowStarted();

        if (scopeEntryEffects.Count > 0)
        {
            LogRootScopeListenersArmed(scopeEntryEffects.Count);
            await PerformEffects(scopeEntryEffects);
        }

        await RunExecutionLoop();
        await ProcessPendingEvents();
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

    public async Task SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId)
    {
        await EnsureExecution();
        _execution!.SetParentInfo(parentWorkflowInstanceId, parentActivityId);
        LogParentInfoSet(parentWorkflowInstanceId, parentActivityId);
        await ProcessPendingEvents();
    }

    public async Task SetInitialVariables(ExpandoObject variables)
    {
        EnsureVariablesWithinLimit(variables);
        await EnsureExecution();

        _execution!.MergeVariables(State.GetRootVariablesId(), variables);
        LogInitialVariablesSet();
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
