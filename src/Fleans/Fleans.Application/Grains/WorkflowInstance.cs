using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Application.Adapters;
using Fleans.Application.Effects;
using Fleans.Application.Logging;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.EventSourcing;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Runtime;
using System.Collections.Concurrent;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance :
    JournaledGrain<WorkflowInstanceState, IDomainEvent>,
    IWorkflowInstanceGrain,
    ICustomStorageInterface<WorkflowInstanceState, IDomainEvent>
{
    public ValueTask<Guid> GetWorkflowInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());
    private IWorkflowDefinition? _workflowDefinition;
    private WorkflowExecution? _execution;

    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowInstance> _logger;
    private readonly IWorkflowQueryService _queryService;
    private readonly IEventStore _eventStore;
    private readonly EffectDispatcher _effectDispatcher;

    private readonly ConcurrentQueue<(PendingExternalEvent Event, TaskCompletionSource Completion)> _pendingExternalEvents = new();
    private IGrainTimer? _pendingEventsTimer;

    /// <summary>
    /// Guards against double-apply: true while draining aggregate events via RaiseEvent.
    /// When true, TransitionState skips event application (aggregate already applied).
    /// </summary>
    private bool _draining;

    /// <summary>
    /// Tracks the version at which the last snapshot was written, for periodic snapshotting.
    /// </summary>
    private int _lastSnapshotVersion;

    /// <summary>
    /// Temporary aggregate used during JournaledGrain activation replay.
    /// TransitionState uses this to apply events when not in drain mode.
    /// </summary>
    private WorkflowExecution? _replayAggregate;

    public WorkflowInstance(
        IGrainFactory grainFactory,
        ILogger<WorkflowInstance> logger,
        IWorkflowQueryService queryService,
        IEventStore eventStore,
        EffectDispatcher effectDispatcher)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _queryService = queryService;
        _eventStore = eventStore;
        _effectDispatcher = effectDispatcher;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        // _replayAggregate lifecycle: base.OnActivateAsync() calls ReadStateFromStorage()
        // then replays events via TransitionState(), which creates _replayAggregate on first call.
        // After base returns, we clear it — the real _execution aggregate will be created
        // by EnsureExecution() with the full workflow definition.
        _replayAggregate = null;

        if (State.IsStarted && State.UserTasks.Count == 0)
        {
            await RehydrateUserTasks();
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // Write a final snapshot on graceful deactivation to reduce replay on next activation
        if (State.IsStarted && this.Version > _lastSnapshotVersion)
        {
            var grainId = this.GetPrimaryKey().ToString();
            await _eventStore.WriteSnapshotAsync(grainId, this.Version, this.State);
            _lastSnapshotVersion = this.Version;
        }

        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    // ── JournaledGrain: TransitionState ─────────────────────────────────

    protected override void TransitionState(
        WorkflowInstanceState state, IDomainEvent @event)
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
        var grainId = this.GetPrimaryKey().ToString();
        var (snapshot, snapshotVersion) = await _eventStore.ReadSnapshotAsync(grainId);
        _lastSnapshotVersion = snapshotVersion;

        var state = snapshot ?? new WorkflowInstanceState();

        // Replay events after the snapshot to recover state lost by ungraceful shutdown.
        // Note: Apply methods with Guid.NewGuid()/DateTimeOffset.UtcNow produce non-deterministic
        // values during replay (e.g. ApplyWorkflowStarted). This is a known limitation —
        // in practice, snapshots are written on graceful deactivation covering most events.
        // A future fix should embed deterministic IDs in event records.
        var events = await _eventStore.ReadEventsAsync(grainId, snapshotVersion);
        if (events.Count > 0)
        {
            var replay = new WorkflowExecution(state);
            foreach (var evt in events)
                replay.ReplayEvent(evt);
        }

        return new(snapshotVersion + events.Count, state);
    }

    public async Task<bool> ApplyUpdatesToStorage(
        IReadOnlyList<IDomainEvent> updates, int expectedVersion)
    {
        var grainId = this.GetPrimaryKey().ToString();
        // expectedVersion is the confirmed version BEFORE these updates.
        // Events should be appended starting at this version.
        var success = await _eventStore.AppendEventsAsync(grainId, updates, expectedVersion);
        if (!success) return false;

        // Project state to query store so IWorkflowQueryService has up-to-date data
        await _eventStore.ProjectStateAsync(grainId, this.State);

        // Periodic snapshot every 100 events (for faster activation recovery)
        var newVersion = expectedVersion + updates.Count;
        if (newVersion - _lastSnapshotVersion >= 100)
        {
            await _eventStore.WriteSnapshotAsync(grainId, newVersion, this.State);
            _lastSnapshotVersion = newVersion;
        }

        return true;
    }

    private async Task RehydrateUserTasks()
    {
        var workflowInstanceId = this.GetPrimaryKey();
        var tasks = await _queryService.GetActiveUserTasksForWorkflow(workflowInstanceId);

        foreach (var task in tasks)
        {
            var metadata = new UserTaskMetadata();
            metadata.Initialize(task.Assignee, task.CandidateGroups,
                task.CandidateUsers, task.ExpectedOutputVariables);

            if (task.TaskState == UserTaskLifecycleState.Claimed && task.ClaimedBy is not null)
            {
                metadata.Claim(task.ClaimedBy, task.ClaimedAt ?? DateTimeOffset.UtcNow);
            }

            State.UserTasks[task.ActivityInstanceId] = metadata;
        }
    }

    private async ValueTask EnsureExecution()
    {
        await EnsureWorkflowDefinitionAsync();
        _execution ??= new WorkflowExecution(State, _workflowDefinition!);
    }

    public async Task SetWorkflow(IWorkflowDefinition workflow, string? startActivityId = null)
    {
        if (_workflowDefinition is not null) throw new ArgumentException("Workflow already set");

        _workflowDefinition = workflow ?? throw new ArgumentNullException(nameof(workflow));

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowDefinitionSet();

        _execution = new WorkflowExecution(State, workflow);
        _execution.Start(startActivityId);
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

        var processDefId = State.ProcessDefinitionId
            ?? throw new InvalidOperationException("ProcessDefinitionId not set — call SetWorkflow first.");

        var key = ProcessDefinition.ExtractKeyFromId(processDefId);
        var grain = _grainFactory.GetGrain<IProcessDefinitionGrain>(key);
        _workflowDefinition = await grain.GetDefinitionById(processDefId);
    }

    private void SetWorkflowRequestContext()
    {
        if (_workflowDefinition is null) return;

        RequestContext.Set(WorkflowContextKeys.WorkflowId, _workflowDefinition.WorkflowId);
        RequestContext.Set(WorkflowContextKeys.WorkflowInstanceId, this.GetPrimaryKey().ToString());
        if (_workflowDefinition.ProcessDefinitionId is not null)
            RequestContext.Set(WorkflowContextKeys.ProcessDefinitionId, _workflowDefinition.ProcessDefinitionId);
    }

    private IDisposable? BeginWorkflowScope()
    {
        if (_workflowDefinition is null) return null;

        return _logger.BeginScope(
            "[{WorkflowId}, {ProcessDefinitionId}, {WorkflowInstanceId}]",
            _workflowDefinition.WorkflowId, _workflowDefinition.ProcessDefinitionId ?? "-", this.GetPrimaryKey().ToString());
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

    public async Task FailActivity(string activityId, Guid activityInstanceId, Exception exception)
    {
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

        var effects = _execution!.FailActivity(activityId, activityInstanceId, exception);
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
        await EnsureExecution();

        _execution!.MergeVariables(State.GetRootVariablesId(), variables);
        LogInitialVariablesSet();
        await ProcessPendingEvents();
    }

    public Task OnChildWorkflowCompleted(string parentActivityId, ExpandoObject childVariables)
    {
        LogChildWorkflowCompletedQueued(parentActivityId);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingExternalEvents.Enqueue((new PendingChildCompleted(parentActivityId, childVariables), tcs));
        EnsurePendingEventsTimerRegistered();
        return tcs.Task;
    }

    public Task OnChildWorkflowFailed(string parentActivityId, Exception exception)
    {
        LogChildWorkflowFailedQueued(parentActivityId);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingExternalEvents.Enqueue((new PendingChildFailed(parentActivityId, exception), tcs));
        EnsurePendingEventsTimerRegistered();
        return tcs.Task;
    }

    // ── Event Handling ──────────────────────────────────────────────────

    public async Task<TimeSpan?> HandleTimerFired(string timerActivityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogTimerReminderFired(timerActivityId);

        var effects = _execution!.HandleTimerFired(timerActivityId, hostActivityInstanceId);

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

    public Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId)
    {
        LogSignalDeliveryQueued(activityId, hostActivityInstanceId);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingExternalEvents.Enqueue((new PendingSignalDelivery(activityId, hostActivityInstanceId), tcs));
        EnsurePendingEventsTimerRegistered();
        return tcs.Task;
    }

    public Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        LogBoundarySignalFiredQueued(boundaryActivityId, hostActivityInstanceId);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingExternalEvents.Enqueue((new PendingBoundarySignalFired(boundaryActivityId, hostActivityInstanceId), tcs));
        EnsurePendingEventsTimerRegistered();
        return tcs.Task;
    }

    // ── User Task Lifecycle ──────────────────────────────────────────────

    public async Task ClaimUserTask(Guid activityInstanceId, string userId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogUserTaskClaimAttempt(activityInstanceId, userId);

        var effects = _execution!.ClaimUserTask(activityInstanceId, userId);
        await PerformEffects(effects);
        await ProcessPendingEvents();
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

    // ── State Facade ────────────────────────────────────────────────────

    public ValueTask<object?> GetVariable(Guid variablesId, string variableName)
        => ValueTask.FromResult(State.GetVariable(variablesId, variableName));

    public ValueTask<ExpandoObject> GetVariables(Guid variablesStateId)
        => ValueTask.FromResult(State.GetMergedVariables(variablesStateId));

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

    public ValueTask<IReadOnlyDictionary<Guid, TransactionOutcomeRecord>> GetTransactionOutcomes()
    {
        IReadOnlyDictionary<Guid, TransactionOutcomeRecord> result =
            State.TransactionOutcomes.AsReadOnly();
        return ValueTask.FromResult(result);
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

    public ValueTask IncrementComplexGatewayJoinToken(string gatewayActivityId, Guid activityInstanceId, string activationCondition)
    {
        _execution!.CreateOrIncrementComplexGatewayJoinToken(gatewayActivityId, activityInstanceId, activationCondition, State.Id);
        return ValueTask.CompletedTask;
    }

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
    }
}
