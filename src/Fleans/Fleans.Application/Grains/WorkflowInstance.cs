using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
using Fleans.Domain.States;
using Fleans.Application.Adapters;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain
{
    public ValueTask<Guid> GetWorkflowInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());
    private IWorkflowDefinition? _workflowDefinition;
    private WorkflowExecution? _execution;

    private readonly IPersistentState<WorkflowInstanceState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowInstance> _logger;
    private readonly IWorkflowQueryService _queryService;

    private WorkflowInstanceState State => _state.State;

    public WorkflowInstance(
        [PersistentState("state", GrainStorageNames.WorkflowInstances)] IPersistentState<WorkflowInstanceState> state,
        IGrainFactory grainFactory,
        ILogger<WorkflowInstance> logger,
        IWorkflowQueryService queryService)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
        _queryService = queryService;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        if (_state.RecordExists && State.UserTasks.Count == 0)
        {
            await RehydrateUserTasks();
        }
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId)
    {
        await EnsureExecution();
        _execution!.SetParentInfo(parentWorkflowInstanceId, parentActivityId);
        LogParentInfoSet(parentWorkflowInstanceId, parentActivityId);
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task SetInitialVariables(ExpandoObject variables)
    {
        await EnsureExecution();

        _execution!.MergeVariables(State.GetRootVariablesId(), variables);
        LogInitialVariablesSet();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task OnChildWorkflowCompleted(string parentActivityId, ExpandoObject childVariables)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogChildWorkflowCompleted(parentActivityId);

        var effects = _execution!.OnChildWorkflowCompleted(parentActivityId, childVariables);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task OnChildWorkflowFailed(string parentActivityId, Exception exception)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogChildWorkflowFailed(parentActivityId);

        var effects = _execution!.OnChildWorkflowFailed(parentActivityId, exception);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var effects = _execution!.HandleSignalDelivery(activityId, hostActivityInstanceId);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var effects = _execution!.HandleSignalDelivery(boundaryActivityId, hostActivityInstanceId);
        await PerformEffects(effects);
        await ResolveExternalCompletions();
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task UnclaimUserTask(Guid activityInstanceId)
    {
        await EnsureExecution();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogUserTaskUnclaimAttempt(activityInstanceId);

        var effects = _execution!.UnclaimUserTask(activityInstanceId);
        await PerformEffects(effects);
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
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
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public ValueTask<GatewayForkState?> FindForkByToken(Guid tokenId)
        => ValueTask.FromResult(State.FindForkByToken(tokenId));
}
