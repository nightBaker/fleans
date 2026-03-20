using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Application.Adapters;
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

    private readonly ConcurrentQueue<(PendingExternalEvent Event, TaskCompletionSource Completion)> _pendingExternalEvents = new();
    private IGrainTimer? _pendingEventsTimer;

    private int _lastSnapshotVersion;
    private string? _grainId;

    private const int SnapshotFrequency = 100;

    private string GrainId => _grainId ??= this.GetPrimaryKey().ToString();

    public WorkflowInstance(
        IGrainFactory grainFactory,
        ILogger<WorkflowInstance> logger,
        IWorkflowQueryService queryService,
        IEventStore eventStore)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _queryService = queryService;
        _eventStore = eventStore;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        if (State.IsStarted && State.UserTasks.Count == 0)
        {
            await RehydrateUserTasks();
        }
    }

    /// <summary>
    /// JournaledGrain calls this for each event during RaiseEvent.
    /// During normal operation this is a no-op because the aggregate already applied the event.
    /// During replay (ReadStateFromStorage), the aggregate's ReplayEvent handles application.
    /// </summary>
    protected override void TransitionState(WorkflowInstanceState state, IDomainEvent @event)
    {
        // No-op: events are already applied by the aggregate's Emit → Apply path
        // during normal operation, or by ReplayEvent during activation recovery.
    }

    // ── ICustomStorageInterface ─────────────────────────────────────────

    async Task<KeyValuePair<int, WorkflowInstanceState>> ICustomStorageInterface<WorkflowInstanceState, IDomainEvent>.ReadStateFromStorage()
    {
        var (snapshot, snapshotVersion) = await _eventStore.ReadSnapshotAsync(GrainId);

        var state = snapshot ?? new WorkflowInstanceState();
        _lastSnapshotVersion = snapshotVersion;

        // Replay events that occurred after the snapshot
        var events = await _eventStore.ReadEventsAsync(GrainId, snapshotVersion);
        if (events.Count > 0)
        {
            var replayExecution = new WorkflowExecution(state);
            foreach (var evt in events)
                replayExecution.ReplayEvent(evt);
        }

        var currentVersion = snapshotVersion + events.Count;
        return new KeyValuePair<int, WorkflowInstanceState>(currentVersion, state);
    }

    async Task<bool> ICustomStorageInterface<WorkflowInstanceState, IDomainEvent>.ApplyUpdatesToStorage(
        IReadOnlyList<IDomainEvent> updates, int expectedVersion)
    {
        var startVersion = expectedVersion - updates.Count;
        var success = await _eventStore.AppendEventsAsync(GrainId, updates, startVersion);

        if (success && expectedVersion - _lastSnapshotVersion >= SnapshotFrequency)
        {
            await _eventStore.WriteSnapshotAsync(GrainId, expectedVersion, State);
            _lastSnapshotVersion = expectedVersion;
        }

        return success;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // Write a final snapshot on deactivation for faster reactivation
        if (State.IsStarted && this.Version > _lastSnapshotVersion)
        {
            await _eventStore.WriteSnapshotAsync(GrainId, this.Version, State);
            _lastSnapshotVersion = this.Version;
        }

        await base.OnDeactivateAsync(reason, cancellationToken);
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
        _execution ??= new WorkflowExecution(this.State, _workflowDefinition!);
    }

    public async Task SetWorkflow(IWorkflowDefinition workflow, string? startActivityId = null)
    {
        if (_workflowDefinition is not null) throw new ArgumentException("Workflow already set");

        _workflowDefinition = workflow ?? throw new ArgumentNullException(nameof(workflow));

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowDefinitionSet();

        _execution = new WorkflowExecution(this.State, workflow);
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

        var grain = _grainFactory.GetGrain<IProcessDefinitionGrain>(processDefId);
        _workflowDefinition = await grain.GetDefinition();
    }

    private void SetWorkflowRequestContext()
    {
        if (_workflowDefinition is null) return;

        RequestContext.Set("WorkflowId", _workflowDefinition.WorkflowId);
        RequestContext.Set("WorkflowInstanceId", this.GetPrimaryKey().ToString());
        if (_workflowDefinition.ProcessDefinitionId is not null)
            RequestContext.Set("ProcessDefinitionId", _workflowDefinition.ProcessDefinitionId);
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

        _execution!.MarkExecutionStarted();
        LogWorkflowStarted();

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

    public async ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        await EnsureExecution();
        _execution!.EvaluateConditionSequence(activityInstanceId, sequenceId, result);
        await ProcessPendingEvents();
    }

    public ValueTask<GatewayForkState?> FindForkByToken(Guid tokenId)
        => ValueTask.FromResult(State.FindForkByToken(tokenId));
}
