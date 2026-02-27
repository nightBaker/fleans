using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Fleans.Application.Services;
using Fleans.Application.WorkflowFactory;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain, IBoundaryEventStateAccessor
{
    public ValueTask<Guid> GetWorkflowInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());
    private IWorkflowDefinition? _workflowDefinition;

    private readonly IPersistentState<WorkflowInstanceState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowInstance> _logger;
    private readonly IBoundaryEventHandler _boundaryHandler;

    private WorkflowInstanceState State => _state.State;

    // IBoundaryEventStateAccessor
    WorkflowInstanceState IBoundaryEventStateAccessor.State => State;
    IGrainFactory IBoundaryEventStateAccessor.GrainFactory => _grainFactory;
    ILogger IBoundaryEventStateAccessor.Logger => _logger;
    IWorkflowExecutionContext IBoundaryEventStateAccessor.WorkflowExecutionContext => this;

    async ValueTask<object?> IBoundaryEventStateAccessor.GetVariable(Guid variablesId, string variableName) => await GetVariable(variablesId, variableName);
    async Task IBoundaryEventStateAccessor.TransitionToNextActivity() => await TransitionToNextActivity();
    async Task IBoundaryEventStateAccessor.ExecuteWorkflow() => await ExecuteWorkflow();
    async Task IBoundaryEventStateAccessor.CancelScopeChildren(Guid scopeId) => await CancelScopeChildren(scopeId);

    public WorkflowInstance(
        [PersistentState("state", GrainStorageNames.WorkflowInstances)] IPersistentState<WorkflowInstanceState> state,
        IGrainFactory grainFactory,
        ILogger<WorkflowInstance> logger,
        IBoundaryEventHandler boundaryHandler)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
        _boundaryHandler = boundaryHandler;
        _boundaryHandler.Initialize(this);
    }

    public async Task StartWorkflow()
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowStarted();
        State.Start();
        await ExecuteWorkflow();
        await _state.WriteStateAsync();
    }

    private async Task ExecuteWorkflow()
    {
        var definition = await GetWorkflowDefinition();
        while (await AnyNotExecuting())
        {
            foreach (var activityState in await GetNotExecutingNotCompletedActivities())
            {
                var activityId = await activityState.GetActivityId();
                var scopeDefinition = definition.GetScopeForActivity(activityId);
                var currentActivity = scopeDefinition.GetActivity(activityId);
                SetActivityRequestContext(activityId, activityState);
                LogExecutingActivity(activityId, currentActivity.GetType().Name);
                var commands = await currentActivity.ExecuteAsync(this, activityState, scopeDefinition);
                var currentEntry = State.GetActiveActivities()
                    .First(e => e.ActivityId == activityId && !e.IsCompleted);
                await ProcessCommands(commands, currentEntry, activityState);
            }

            await TransitionToNextActivity();
            LogStatePersistedAfterTransition();
            await _state.WriteStateAsync();
        }
    }

    private async Task ProcessCommands(
        IReadOnlyList<IExecutionCommand> commands,
        ActivityInstanceEntry entry,
        IActivityExecutionContext activityContext)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case CompleteCommand:
                    await activityContext.Complete();
                    break;

                case SpawnActivityCommand spawn:
                    var spawnId = Guid.NewGuid();
                    var spawnInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(spawnId);
                    await spawnInstance.SetActivity(spawn.Activity.ActivityId, spawn.Activity.GetType().Name);
                    var spawnVarsId = await activityContext.GetVariablesStateId();
                    await spawnInstance.SetVariablesId(spawnVarsId);
                    var spawnEntry = new ActivityInstanceEntry(spawnId, spawn.Activity.ActivityId, State.Id, spawn.ScopeId);
                    State.AddEntries([spawnEntry]);
                    break;

                case OpenSubProcessCommand sub:
                    await OpenSubProcessScope(entry.ActivityInstanceId, sub.SubProcess, sub.ParentVariablesId);
                    break;

                case RegisterTimerCommand timer:
                    await RegisterTimerReminder(entry.ActivityInstanceId, timer.TimerActivityId, timer.DueTime);
                    break;

                case RegisterMessageCommand msg:
                    if (msg.IsBoundary)
                        await RegisterBoundaryMessageSubscription(msg.VariablesId,
                            entry.ActivityInstanceId, msg.ActivityId, msg.MessageDefinitionId);
                    else
                        await RegisterMessageSubscription(msg.VariablesId, msg.MessageDefinitionId, msg.ActivityId);
                    break;

                case RegisterSignalCommand sig:
                    if (sig.IsBoundary)
                        await RegisterBoundarySignalSubscription(
                            entry.ActivityInstanceId, sig.ActivityId, sig.SignalName);
                    else
                        await RegisterSignalSubscription(sig.SignalName, sig.ActivityId,
                            entry.ActivityInstanceId);
                    break;

                case StartChildWorkflowCommand child:
                    await StartChildWorkflow(child.CallActivity, activityContext);
                    break;

                case AddConditionsCommand cond:
                    await AddConditionSequenceStates(entry.ActivityInstanceId, cond.SequenceFlowIds);
                    foreach (var eval in cond.Evaluations)
                    {
                        await activityContext.PublishEvent(new Domain.Events.EvaluateConditionEvent(
                            await GetWorkflowInstanceId(),
                            (await GetWorkflowDefinition()).WorkflowId,
                            (await GetWorkflowDefinition()).ProcessDefinitionId,
                            entry.ActivityInstanceId,
                            entry.ActivityId,
                            eval.SequenceFlowId,
                            eval.Condition));
                    }
                    break;

                case ThrowSignalCommand sig:
                    await ThrowSignal(sig.SignalName);
                    break;
            }
        }
    }

    private async Task OpenSubProcessScope(Guid subProcessInstanceId, SubProcess subProcess, Guid parentVariablesId)
    {
        var childVariablesId = State.AddChildVariableState(parentVariablesId);
        LogSubProcessInitialized(subProcess.ActivityId, childVariablesId);

        var startActivity = subProcess.Activities.FirstOrDefault(a => a is StartEvent)
            ?? throw new InvalidOperationException($"SubProcess '{subProcess.ActivityId}' must have a StartEvent");

        var startInstanceId = Guid.NewGuid();
        var startInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(startInstanceId);
        await startInstance.SetActivity(startActivity.ActivityId, startActivity.GetType().Name);
        await startInstance.SetVariablesId(childVariablesId);

        var startEntry = new ActivityInstanceEntry(startInstanceId, startActivity.ActivityId, State.Id, subProcessInstanceId);
        State.AddEntries([startEntry]);
    }

    public async Task CompleteActivity(string activityId, ExpandoObject variables)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompletingActivity(activityId);
        await CompleteActivityState(activityId, variables);
        await ExecuteWorkflow();
        await _state.WriteStateAsync();
    }

    public async Task FailActivity(string activityId, Exception exception)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogFailingActivity(activityId);

        await FailActivityWithBoundaryCheck(activityId, exception);
        await _state.WriteStateAsync();
    }

    public async Task HandleTimerFired(string timerActivityId, Guid hostActivityInstanceId)
    {
        await EnsureWorkflowDefinitionAsync();
        var definition = await GetWorkflowDefinition();

        // Check if this is a boundary timer
        var scopeDef = definition.FindScopeForActivity(timerActivityId);
        var activity = scopeDef?.GetActivity(timerActivityId);
        if (activity is BoundaryTimerEvent boundaryTimer)
        {
            SetWorkflowRequestContext();
            using var scope = BeginWorkflowScope();
            LogTimerReminderFired(timerActivityId);
            await HandleBoundaryTimerFired(boundaryTimer, hostActivityInstanceId);
            await _state.WriteStateAsync();
        }
        else
        {
            SetWorkflowRequestContext();
            using var scope = BeginWorkflowScope();
            LogTimerReminderFired(timerActivityId);

            // Intermediate catch timer — just complete the activity
            // Guard: activity may already be completed by a previous reminder tick
            var entry = State.Entries.FirstOrDefault(e =>
                e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
            if (entry == null)
            {
                LogStaleTimerIgnored(timerActivityId);
                return;
            }

            await CompleteActivityState(timerActivityId, new ExpandoObject());
            await ExecuteWorkflow();
            await _state.WriteStateAsync();
        }
    }

    private Task HandleBoundaryTimerFired(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId)
        => _boundaryHandler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostActivityInstanceId, _workflowDefinition!);

    private async Task<ActivityInstanceEntry> CreateNextActivityEntry(
        Activity sourceActivity, IActivityInstanceGrain sourceInstance,
        Activity nextActivity, Guid? scopeId)
    {
        var sourceVariablesId = await sourceInstance.GetVariablesStateId();
        var variablesId = sourceActivity is ParallelGateway { IsFork: true }
            ? State.AddCloneOfVariableState(sourceVariablesId)
            : sourceVariablesId;
        RequestContext.Set("VariablesId", variablesId.ToString());

        var newId = Guid.NewGuid();
        var newInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(newId);
        await newInstance.SetVariablesId(variablesId);
        await newInstance.SetActivity(nextActivity.ActivityId, nextActivity.GetType().Name);

        return new ActivityInstanceEntry(newId, nextActivity.ActivityId, State.Id, scopeId);
    }

    private async Task TransitionToNextActivity()
    {
        var definition = await GetWorkflowDefinition();
        var newActiveEntries = new List<ActivityInstanceEntry>();
        var completedEntries = new List<ActivityInstanceEntry>();

        foreach (var entry in State.GetActiveActivities().ToList())
        {
            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);

            if (await activityInstance.IsCompleted())
            {
                completedEntries.Add(entry);

                // Failed or cancelled activities don't transition to next activities
                if (await activityInstance.GetErrorState() is not null)
                    continue;

                if (await activityInstance.IsCancelled())
                    continue;

                var scopeDefinition = definition.GetScopeForActivity(entry.ActivityId);
                var currentActivity = scopeDefinition.GetActivity(entry.ActivityId);

                var nextActivities = await currentActivity.GetNextActivities(this, activityInstance, scopeDefinition);

                foreach(var nextActivity in nextActivities)
                {
                    // For join gateways, reuse the existing active entry instead of creating a duplicate
                    if (nextActivity.IsJoinGateway)
                    {
                        var existingEntry = State.GetActiveActivities()
                            .FirstOrDefault(e => e.ActivityId == nextActivity.ActivityId && e.ScopeId == entry.ScopeId);
                        if (existingEntry != null)
                        {
                            LogJoinGatewayDeduplication(nextActivity.ActivityId, existingEntry.ActivityInstanceId);
                            var existingInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(existingEntry.ActivityInstanceId);
                            await existingInstance.ResetExecuting();
                            continue;
                        }
                    }

                    var newEntry = await CreateNextActivityEntry(currentActivity, activityInstance, nextActivity, entry.ScopeId);
                    newActiveEntries.Add(newEntry);
                }
            }
        }

        LogTransition(completedEntries.Count, newActiveEntries.Count);
        LogStateCompleteEntries(completedEntries.Count);
        State.CompleteEntries(completedEntries);
        LogStateAddEntries(newActiveEntries.Count);
        State.AddEntries(newActiveEntries);

        await CompleteFinishedSubProcessScopes(definition);
    }

    private async Task CompleteFinishedSubProcessScopes(IWorkflowDefinition definition)
    {
        const int maxIterations = 100;
        var iteration = 0;
        bool anyCompleted;
        do
        {
            if (++iteration > maxIterations)
                throw new InvalidOperationException("Sub-process completion loop exceeded max iterations — possible cycle in scope graph");

            anyCompleted = false;
            foreach (var entry in State.GetActiveActivities().ToList())
            {
                var scopeDefinition = definition.GetScopeForActivity(entry.ActivityId);
                var activity = scopeDefinition.GetActivity(entry.ActivityId);
                if (activity is not SubProcess) continue;

                var scopeEntries = State.Entries.Where(e => e.ScopeId == entry.ActivityInstanceId).ToList();
                if (scopeEntries.Count == 0) continue;
                if (!scopeEntries.All(e => e.IsCompleted)) continue;

                // All scope children are done — complete the sub-process
                var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
                await activityInstance.Complete();
                LogSubProcessCompleted(entry.ActivityId);

                var nextActivities = await activity.GetNextActivities(this, activityInstance, scopeDefinition);

                var completedEntries = new List<ActivityInstanceEntry> { entry };
                var newEntries = new List<ActivityInstanceEntry>();

                foreach (var nextActivity in nextActivities)
                {
                    var newEntry = await CreateNextActivityEntry(activity, activityInstance, nextActivity, entry.ScopeId);
                    newEntries.Add(newEntry);
                }

                State.CompleteEntries(completedEntries);
                State.AddEntries(newEntries);
                anyCompleted = true;
            }
        } while (anyCompleted);
    }

    public async Task CancelScopeChildren(Guid scopeId)
    {
        var cancelledEntries = new List<ActivityInstanceEntry>();
        foreach (var entry in State.GetActiveActivities().Where(e => e.ScopeId == scopeId).ToList())
        {
            // Recursively cancel nested sub-process children
            if (State.Entries.Any(e => e.ScopeId == entry.ActivityInstanceId && !e.IsCompleted))
            {
                await CancelScopeChildren(entry.ActivityInstanceId);
            }

            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
            await activityInstance.Cancel("Sub-process scope cancelled by boundary event");
            cancelledEntries.Add(entry);
            LogScopeChildCancelled(entry.ActivityId, scopeId);
        }
        State.CompleteEntries(cancelledEntries);
    }

    private async Task CompleteActivityState(string activityId, ExpandoObject variables)
    {
        var entry = State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
        SetActivityRequestContext(activityId, activityInstance);
        await activityInstance.Complete();
        var variablesId = await activityInstance.GetVariablesStateId();
        RequestContext.Set("VariablesId", variablesId.ToString());

        LogStateMergeState(variablesId);
        State.MergeState(variablesId, variables);

        // Unregister any boundary timer reminders attached to this activity
        var definition = await GetWorkflowDefinition();
        await _boundaryHandler.UnregisterBoundaryTimerRemindersAsync(activityId, entry.ActivityInstanceId, definition);

        // Unsubscribe any boundary message subscriptions attached to this activity
        await _boundaryHandler.UnsubscribeBoundaryMessageSubscriptionsAsync(activityId, variablesId, definition);

        // Unsubscribe any boundary signal subscriptions attached to this activity
        await _boundaryHandler.UnsubscribeBoundarySignalSubscriptionsAsync(activityId, definition);

        // Cancel sibling catch events if this activity is after an Event-Based Gateway
        await CancelEventBasedGatewaySiblings(activityId, definition);
    }

    private async Task CancelEventBasedGatewaySiblings(string completedActivityId, IWorkflowDefinition definition)
    {
        var siblingActivityIds = definition.GetEventBasedGatewaySiblings(completedActivityId);
        if (siblingActivityIds.Count == 0) return;

        foreach (var siblingId in siblingActivityIds)
        {
            var entry = State.GetFirstActive(siblingId);
            if (entry is null) continue;

            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
            var siblingActivity = definition.GetActivityAcrossScopes(siblingId);

            // Unsubscribe based on event type
            switch (siblingActivity)
            {
                case TimerIntermediateCatchEvent:
                    var callbackGrain = _grainFactory.GetGrain<ITimerCallbackGrain>(
                        this.GetPrimaryKey(), $"{entry.ActivityInstanceId}:{siblingId}");
                    await callbackGrain.Cancel();
                    break;

                case MessageIntermediateCatchEvent msgCatch:
                    var variablesId = await activityInstance.GetVariablesStateId();
                    var msgDef = definition.Messages.First(m => m.Id == msgCatch.MessageDefinitionId);
                    if (msgDef.CorrelationKeyExpression is not null)
                    {
                        var corrValue = await GetVariable(variablesId, msgDef.CorrelationKeyExpression);
                        if (corrValue is not null)
                        {
                            var corrGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(msgDef.Name);
                            await corrGrain.Unsubscribe(corrValue.ToString()!);
                        }
                    }
                    break;

                case SignalIntermediateCatchEvent sigCatch:
                    var sigDef = definition.Signals.First(s => s.Id == sigCatch.SignalDefinitionId);
                    var sigGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(sigDef.Name);
                    await sigGrain.Unsubscribe(this.GetPrimaryKey(), siblingId);
                    break;
            }

            await activityInstance.Cancel("Event-based gateway: sibling event completed");
            LogEventBasedGatewaySiblingCancelled(siblingId, completedActivityId);
        }
    }

    private async Task FailActivityState(string activityId, Exception exception)
    {
        var entry = State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
        SetActivityRequestContext(activityId, activityInstance);
        await activityInstance.Fail(exception);
    }
    
    public async ValueTask Complete()
    {
        State.Complete();
        LogStateCompleted();
        await _state.WriteStateAsync();

        // Notify parent if this is a child workflow
        if (State.ParentWorkflowInstanceId.HasValue)
        {
            var childVariables = State.VariableStates.Count > 0
                ? State.VariableStates[0].Variables
                : new ExpandoObject();

            var parent = _grainFactory.GetGrain<IWorkflowInstanceGrain>(State.ParentWorkflowInstanceId.Value);
            await parent.OnChildWorkflowCompleted(State.ParentActivityId!, childVariables);
        }
    }

    public async Task SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId)
    {
        State.SetParentInfo(parentWorkflowInstanceId, parentActivityId);
        LogParentInfoSet(parentWorkflowInstanceId, parentActivityId);
        await _state.WriteStateAsync();
    }

    public async Task SetInitialVariables(ExpandoObject variables)
    {
        if (State.VariableStates.Count == 0)
            throw new InvalidOperationException("Call SetWorkflow before SetInitialVariables.");

        State.MergeState(State.VariableStates[0].Id, variables);
        LogInitialVariablesSet();
        await _state.WriteStateAsync();
    }

    private async Task StartChildWorkflow(CallActivity callActivity, IActivityExecutionContext activityContext)
    {
        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        var childDefinition = await factory.GetLatestWorkflowDefinition(callActivity.CalledProcessKey);

        var childId = Guid.NewGuid();
        var child = _grainFactory.GetGrain<IWorkflowInstanceGrain>(childId);

        LogStartingChildWorkflow(callActivity.CalledProcessKey, childId);

        await child.SetWorkflow(childDefinition);
        await child.SetParentInfo(this.GetPrimaryKey(), callActivity.ActivityId);

        // Map input variables
        var activityInstanceId = await activityContext.GetActivityInstanceId();
        var activityGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(activityInstanceId);
        var variablesId = await activityGrain.GetVariablesStateId();
        var parentVariables = State.GetVariableState(variablesId).Variables;
        var childInputVars = callActivity.BuildChildInputVariables(parentVariables);

        if (((IDictionary<string, object?>)childInputVars).Count > 0)
            await child.SetInitialVariables(childInputVars);

        // Record child reference on parent's activity entry
        var entry = State.GetFirstActive(callActivity.ActivityId)
            ?? throw new InvalidOperationException($"Active entry not found for '{callActivity.ActivityId}'");
        entry.SetChildWorkflowInstanceId(childId);

        // Start child — completion notifies parent via direct grain call
        await child.StartWorkflow();

        await _state.WriteStateAsync();
    }

    public async Task OnChildWorkflowCompleted(string parentActivityId, ExpandoObject childVariables)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogChildWorkflowCompleted(parentActivityId);

        var definition = await GetWorkflowDefinition();
        var callActivity = definition.GetActivityAcrossScopes(parentActivityId) as CallActivity
            ?? throw new InvalidOperationException($"Activity '{parentActivityId}' is not a CallActivity");

        var mappedOutput = callActivity.BuildParentOutputVariables(childVariables);
        await CompleteActivityState(parentActivityId, mappedOutput);
        await ExecuteWorkflow();
        await _state.WriteStateAsync();
    }

    public async Task OnChildWorkflowFailed(string parentActivityId, Exception exception)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogChildWorkflowFailed(parentActivityId);

        await FailActivityWithBoundaryCheck(parentActivityId, exception);
        await _state.WriteStateAsync();
    }

    public async Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogConditionResult(conditionSequenceId, result);

        var entry = State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);

        var definition = await GetWorkflowDefinition();
        var gateway = definition.GetActivityAcrossScopes(activityId) as ConditionalGateway
            ?? throw new InvalidOperationException("Activity is not a conditional gateway");

        bool isDecisionMade;
        try
        {
            isDecisionMade = await gateway.SetConditionResult(
                this, activityInstance, conditionSequenceId, result, definition);
        }
        catch (InvalidOperationException)
        {
            LogGatewayNoDefaultFlow(activityId);
            throw;
        }

        if (isDecisionMade)
        {
            if (result)
                LogGatewayShortCircuited(activityId, conditionSequenceId);
            else
                LogGatewayTakingDefaultFlow(activityId);

            LogGatewayAutoCompleting(activityId);
            await activityInstance.Complete();
            await ExecuteWorkflow();
        }

        await _state.WriteStateAsync();
    }

    public async Task SetWorkflow(IWorkflowDefinition workflow)
    {
        if(_workflowDefinition is not null) throw new ArgumentException("Workflow already set");

        _workflowDefinition = workflow ?? throw new ArgumentNullException(nameof(workflow));

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowDefinitionSet();

        var startActivity = workflow.Activities.FirstOrDefault(a => a is StartEvent or TimerStartEvent)
            ?? throw new InvalidOperationException("Workflow must have a StartEvent or TimerStartEvent");

        var activityInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(activityInstanceId);
        await activityInstance.SetActivity(startActivity.ActivityId, startActivity.GetType().Name);
        await activityInstance.SetVariablesId(variablesId);

        var entry = new ActivityInstanceEntry(activityInstanceId, startActivity.ActivityId, this.GetPrimaryKey());
        LogStateStartWith(startActivity.ActivityId);
        State.StartWith(this.GetPrimaryKey(), workflow.ProcessDefinitionId, entry, variablesId);
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

    public ValueTask<ExpandoObject> GetVariables(Guid variablesStateId)
        => ValueTask.FromResult(State.GetMergedVariables(variablesStateId));

    // State facade methods — activities access state through these, not directly
    public ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities()
    {
        IReadOnlyList<IActivityExecutionContext> result = State.GetActiveActivities()
            .Select(e => (IActivityExecutionContext)_grainFactory.GetGrain<IActivityInstanceGrain>(e.ActivityInstanceId))
            .ToList().AsReadOnly();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities()
    {
        IReadOnlyList<IActivityExecutionContext> result = State.GetCompletedActivities()
            .Select(e => (IActivityExecutionContext)_grainFactory.GetGrain<IActivityInstanceGrain>(e.ActivityInstanceId))
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

    private async Task AddConditionSequenceStates(Guid activityInstanceId, string[] sequenceFlowIds)
    {
        State.AddConditionSequenceStates(activityInstanceId, sequenceFlowIds);
        await _state.WriteStateAsync();
    }

    public async ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        State.SetConditionSequenceResult(activityInstanceId, sequenceId, result);
        await _state.WriteStateAsync();
    }


    private async Task<bool> AnyNotExecuting()
    {
        foreach (var entry in State.GetActiveActivities().ToList())
        {
            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
            if (!await activityInstance.IsExecuting())
                return true;
        }
        return false;
    }

    private async Task<IActivityInstanceGrain[]> GetNotExecutingNotCompletedActivities()
    {
        var result = new List<IActivityInstanceGrain>();
        foreach (var entry in State.GetActiveActivities().ToList())
        {
            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
            if (!await activityInstance.IsExecuting() && !await activityInstance.IsCompleted())
                result.Add(activityInstance);
        }
        return result.ToArray();
    }

    private async Task FailActivityWithBoundaryCheck(string activityId, Exception exception)
    {
        await FailActivityState(activityId, exception);

        var definition = await GetWorkflowDefinition();
        var activityEntry = State.GetFirstActive(activityId) ?? State.Entries.Last(e => e.ActivityId == activityId);
        var activityGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(activityEntry.ActivityInstanceId);
        var errorState = await activityGrain.GetErrorState();

        if (errorState is null)
        {
            // errorState is never null after FailActivityState — defensive guard only
            await ExecuteWorkflow();
            return;
        }

        var match = definition.FindBoundaryErrorHandler(activityId, errorState.Code.ToString());
        if (match is null)
        {
            // No boundary handler — complete the failed entry so the parent scope stays active
            // (its executing SubProcess grain prevents ExecuteWorkflow from auto-completing)
            var failedEntry = State.Entries.Last(e => e.ActivityId == activityId);
            State.CompleteEntries([failedEntry]);
            await ExecuteWorkflow();

            // If this is a child workflow with no remaining active activities,
            // propagate the failure to the parent (mirrors Complete() success path)
            if (State.ParentWorkflowInstanceId.HasValue && !State.GetActiveActivities().Any())
            {
                LogChildFailurePropagatedToParent(State.ParentActivityId!);
                var parent = _grainFactory.GetGrain<IWorkflowInstanceGrain>(State.ParentWorkflowInstanceId.Value);
                await parent.OnChildWorkflowFailed(State.ParentActivityId!, exception);
            }

            return;
        }

        var (boundaryEvent, scopeDefinition, attachedToActivityId) = match.Value;

        if (attachedToActivityId == activityId)
        {
            // Direct match — boundary is on the failed activity itself
            await _boundaryHandler.HandleBoundaryErrorAsync(activityId, boundaryEvent, activityEntry.ActivityInstanceId, scopeDefinition);
        }
        else
        {
            // Bubbled up — cancel intermediate scopes between failed activity and matched SubProcess
            var failedEntry = State.Entries.Last(e => e.ActivityId == activityId);
            State.CompleteEntries([failedEntry]);

            var currentScopeId = failedEntry.ScopeId;
            while (currentScopeId.HasValue)
            {
                var scopeEntry = State.Entries.First(e => e.ActivityInstanceId == currentScopeId.Value);

                if (scopeEntry.ActivityId == attachedToActivityId)
                {
                    // Found the SubProcess the boundary is attached to — cancel and handle
                    await CancelScopeChildren(currentScopeId.Value);
                    var scopeInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(scopeEntry.ActivityInstanceId);
                    await scopeInstance.Cancel("Sub-process interrupted by error boundary");
                    State.CompleteEntries([scopeEntry]);
                    await _boundaryHandler.HandleBoundaryErrorAsync(scopeEntry.ActivityId, boundaryEvent, scopeEntry.ActivityInstanceId, scopeDefinition);
                    return;
                }

                currentScopeId = scopeEntry.ScopeId;
            }
        }
    }

    private void SetWorkflowRequestContext()
    {
        if (_workflowDefinition is null) return;

        RequestContext.Set("WorkflowId", _workflowDefinition.WorkflowId);
        RequestContext.Set("WorkflowInstanceId", this.GetPrimaryKey().ToString());
        if (_workflowDefinition.ProcessDefinitionId is not null)
            RequestContext.Set("ProcessDefinitionId", _workflowDefinition.ProcessDefinitionId);
    }

    private void SetActivityRequestContext(string activityId, IActivityInstanceGrain activityInstance)
    {
        RequestContext.Set("ActivityId", activityId);
        RequestContext.Set("ActivityInstanceId", activityInstance.GetPrimaryKey().ToString());
    }

    private IDisposable? BeginWorkflowScope()
    {
        if (_workflowDefinition is null) return null;

        return _logger.BeginScope(
            "[{WorkflowId}, {ProcessDefinitionId}, {WorkflowInstanceId}]",
            _workflowDefinition.WorkflowId, _workflowDefinition.ProcessDefinitionId ?? "-", this.GetPrimaryKey().ToString());
    }

    public ValueTask<object?> GetVariable(Guid variablesId, string variableName)
        => ValueTask.FromResult(State.GetVariable(variablesId, variableName));

    private async Task RegisterMessageSubscription(Guid variablesId, string messageDefinitionId, string activityId)
    {
        var definition = await GetWorkflowDefinition();
        var messageDef = definition.Messages.First(m => m.Id == messageDefinitionId);

        if (messageDef.CorrelationKeyExpression is null)
            throw new InvalidOperationException(
                $"Message '{messageDef.Name}' has no correlationKeyExpression — cannot auto-subscribe.");

        var entry = State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException($"Active entry not found for '{activityId}'");

        var correlationValue = await GetVariable(variablesId, messageDef.CorrelationKeyExpression);
        var correlationKey = correlationValue?.ToString()
            ?? throw new InvalidOperationException(
                $"Correlation variable '{messageDef.CorrelationKeyExpression}' is null for message '{messageDef.Name}'.");

        await _state.WriteStateAsync();

        var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);

        try
        {
            await correlationGrain.Subscribe(correlationKey, this.GetPrimaryKey(), activityId, entry.ActivityInstanceId);
        }
        catch (Exception ex)
        {
            LogMessageSubscriptionFailed(activityId, messageDef.Name, correlationKey, ex);
            await FailActivityWithBoundaryCheck(activityId, ex);
            await _state.WriteStateAsync();
            return;
        }

        LogMessageSubscriptionRegistered(activityId, messageDef.Name, correlationKey);
    }

    private async Task RegisterTimerReminder(Guid hostActivityInstanceId, string timerActivityId, TimeSpan dueTime)
    {
        var callbackGrain = _grainFactory.GetGrain<ITimerCallbackGrain>(
            this.GetPrimaryKey(), $"{hostActivityInstanceId}:{timerActivityId}");
        await callbackGrain.Activate(dueTime);
        LogTimerReminderRegistered(timerActivityId, dueTime);
    }

    private async Task RegisterBoundaryMessageSubscription(Guid variablesId, Guid hostActivityInstanceId, string boundaryActivityId, string messageDefinitionId)
    {
        var definition = await GetWorkflowDefinition();
        var messageDef = definition.Messages.First(m => m.Id == messageDefinitionId);

        if (messageDef.CorrelationKeyExpression is null)
            return;

        var correlationValue = await GetVariable(variablesId, messageDef.CorrelationKeyExpression);
        if (correlationValue is null)
            return;

        var correlationKey = correlationValue.ToString()!;
        var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);

        try
        {
            await correlationGrain.Subscribe(correlationKey, this.GetPrimaryKey(), boundaryActivityId, hostActivityInstanceId);
        }
        catch (Exception ex)
        {
            LogMessageSubscriptionFailed(boundaryActivityId, messageDef.Name, correlationKey, ex);
            return;
        }

        LogMessageSubscriptionRegistered(boundaryActivityId, messageDef.Name, correlationKey);
    }

    public async Task HandleMessageDelivery(string activityId, Guid hostActivityInstanceId, ExpandoObject variables)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var definition = await GetWorkflowDefinition();
        var scopeDef = definition.GetScopeForActivity(activityId);
        var activity = scopeDef.GetActivity(activityId);

        if (activity is MessageBoundaryEvent boundaryMessage)
        {
            LogMessageDeliveryBoundary(activityId);
            await _boundaryHandler.HandleBoundaryMessageFiredAsync(boundaryMessage, hostActivityInstanceId, definition);
        }
        else
        {
            LogMessageDeliveryComplete(activityId);
            await CompleteActivityState(activityId, variables);
            await ExecuteWorkflow();
        }

        await _state.WriteStateAsync();
    }

    public async Task HandleBoundaryMessageFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var definition = await GetWorkflowDefinition();
        var boundaryMessage = definition.GetActivityAcrossScopes(boundaryActivityId) as MessageBoundaryEvent
            ?? throw new InvalidOperationException($"Activity '{boundaryActivityId}' is not a MessageBoundaryEvent");

        await _boundaryHandler.HandleBoundaryMessageFiredAsync(boundaryMessage, hostActivityInstanceId, definition);
        await _state.WriteStateAsync();
    }

    private async Task RegisterSignalSubscription(string signalName, string activityId, Guid activityInstanceId)
    {
        await _state.WriteStateAsync();

        var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalName);

        try
        {
            await signalGrain.Subscribe(this.GetPrimaryKey(), activityId, activityInstanceId);
        }
        catch (Exception ex)
        {
            LogSignalSubscriptionFailed(activityId, signalName, ex);
            await FailActivityWithBoundaryCheck(activityId, ex);
            await _state.WriteStateAsync();
            return;
        }

        LogSignalSubscriptionRegistered(activityId, signalName);
    }

    private async Task RegisterBoundarySignalSubscription(Guid hostActivityInstanceId, string boundaryActivityId, string signalName)
    {
        var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalName);

        try
        {
            await signalGrain.Subscribe(this.GetPrimaryKey(), boundaryActivityId, hostActivityInstanceId);
        }
        catch (Exception ex)
        {
            LogSignalSubscriptionFailed(boundaryActivityId, signalName, ex);
            return;
        }

        LogSignalSubscriptionRegistered(boundaryActivityId, signalName);
    }

    private async Task ThrowSignal(string signalName)
    {
        var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalName);
        var deliveredCount = await signalGrain.BroadcastSignal();
        LogSignalThrown(signalName, deliveredCount);
    }

    public async Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var definition = await GetWorkflowDefinition();
        var scopeDef = definition.GetScopeForActivity(activityId);
        var activity = scopeDef.GetActivity(activityId);

        if (activity is SignalBoundaryEvent boundarySignal)
        {
            LogSignalDeliveryBoundary(activityId);
            await _boundaryHandler.HandleBoundarySignalFiredAsync(boundarySignal, hostActivityInstanceId, definition);
        }
        else
        {
            LogSignalDeliveryComplete(activityId);
            await CompleteActivityState(activityId, new ExpandoObject());
            await ExecuteWorkflow();
        }

        await _state.WriteStateAsync();
    }

    public async Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var definition = await GetWorkflowDefinition();
        var boundarySignal = definition.GetActivityAcrossScopes(boundaryActivityId) as SignalBoundaryEvent
            ?? throw new InvalidOperationException($"Activity '{boundaryActivityId}' is not a SignalBoundaryEvent");

        await _boundaryHandler.HandleBoundarySignalFiredAsync(boundarySignal, hostActivityInstanceId, definition);
        await _state.WriteStateAsync();
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Workflow definition set")]
    private partial void LogWorkflowDefinitionSet();

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Workflow execution started")]
    private partial void LogWorkflowStarted();

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Executing activity {ActivityId} ({ActivityType})")]
    private partial void LogExecutingActivity(string activityId, string activityType);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Completing activity {ActivityId}")]
    private partial void LogCompletingActivity(string activityId);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning, Message = "Failing activity {ActivityId}")]
    private partial void LogFailingActivity(string activityId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug, Message = "Condition sequence result: {SequenceFlowId}={Result}")]
    private partial void LogConditionResult(string sequenceFlowId, bool result);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug, Message = "Transitioning: {CompletedCount} completed, {NewCount} new")]
    private partial void LogTransition(int completedCount, int newCount);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug, Message = "Gateway {ActivityId} decision made, auto-completing and resuming workflow")]
    private partial void LogGatewayAutoCompleting(string activityId);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Debug, Message = "Gateway {ActivityId} short-circuited: condition {ConditionSequenceFlowId} is true")]
    private partial void LogGatewayShortCircuited(string activityId, string conditionSequenceFlowId);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Debug, Message = "Gateway {ActivityId} all conditions false, taking default flow")]
    private partial void LogGatewayTakingDefaultFlow(string activityId);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Error, Message = "Gateway {ActivityId} all conditions false and no default flow — misconfigured workflow")]
    private partial void LogGatewayNoDefaultFlow(string activityId);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Workflow initialized with start activity {ActivityId}")]
    private partial void LogStateStartWith(string activityId);
    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Workflow completed")]
    private partial void LogStateCompleted();

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug, Message = "Variables merged for state {VariablesId}")]
    private partial void LogStateMergeState(Guid variablesId);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Debug, Message = "Adding {Count} entries")]
    private partial void LogStateAddEntries(int count);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Debug, Message = "Completing {Count} activities")]
    private partial void LogStateCompleteEntries(int count);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Debug, Message = "State persisted after transition")]
    private partial void LogStatePersistedAfterTransition();

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Parent info set: ParentWorkflowInstanceId={ParentWorkflowInstanceId}, ParentActivityId={ParentActivityId}")]
    private partial void LogParentInfoSet(Guid parentWorkflowInstanceId, string parentActivityId);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Debug, Message = "Initial variables set")]
    private partial void LogInitialVariablesSet();

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information, Message = "Starting child workflow: CalledProcessKey={CalledProcessKey}, ChildId={ChildId}")]
    private partial void LogStartingChildWorkflow(string calledProcessKey, Guid childId);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Information, Message = "Child workflow completed for CallActivity {ParentActivityId}")]
    private partial void LogChildWorkflowCompleted(string parentActivityId);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Warning, Message = "Child workflow failed for CallActivity {ParentActivityId}")]
    private partial void LogChildWorkflowFailed(string parentActivityId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Child workflow failed with no boundary handler, propagating error to parent. ParentActivityId={ParentActivityId}")]
    private partial void LogChildFailurePropagatedToParent(string parentActivityId);

    [LoggerMessage(EventId = 1017, Level = LogLevel.Information, Message = "Timer reminder registered for activity {TimerActivityId}, due in {DueTime}")]
    private partial void LogTimerReminderRegistered(string timerActivityId, TimeSpan dueTime);

    [LoggerMessage(EventId = 1018, Level = LogLevel.Information, Message = "Timer reminder fired for activity {TimerActivityId}")]
    private partial void LogTimerReminderFired(string timerActivityId);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Information,
        Message = "Message subscription registered for activity {ActivityId}: messageName={MessageName}, correlationKey={CorrelationKey}")]
    private partial void LogMessageSubscriptionRegistered(string activityId, string messageName, string correlationKey);

    [LoggerMessage(EventId = 1023, Level = LogLevel.Warning,
        Message = "Message subscription failed for activity {ActivityId}: messageName={MessageName}, correlationKey={CorrelationKey}")]
    private partial void LogMessageSubscriptionFailed(string activityId, string messageName, string correlationKey, Exception exception);

    [LoggerMessage(EventId = 1024, Level = LogLevel.Debug, Message = "Stale timer ignored for activity {TimerActivityId} — activity no longer active")]
    private partial void LogStaleTimerIgnored(string timerActivityId);

    [LoggerMessage(EventId = 1025, Level = LogLevel.Information, Message = "Message delivered as boundary event for activity {ActivityId}")]
    private partial void LogMessageDeliveryBoundary(string activityId);

    [LoggerMessage(EventId = 1026, Level = LogLevel.Information, Message = "Message delivered, completing activity {ActivityId}")]
    private partial void LogMessageDeliveryComplete(string activityId);

    [LoggerMessage(EventId = 1027, Level = LogLevel.Debug, Message = "Join gateway {ActivityId} already active ({ActivityInstanceId}), reusing entry instead of creating duplicate")]
    private partial void LogJoinGatewayDeduplication(string activityId, Guid activityInstanceId);

    [LoggerMessage(EventId = 1028, Level = LogLevel.Information,
        Message = "Signal subscription registered for activity {ActivityId}: signalName={SignalName}")]
    private partial void LogSignalSubscriptionRegistered(string activityId, string signalName);

    [LoggerMessage(EventId = 1029, Level = LogLevel.Warning,
        Message = "Signal subscription failed for activity {ActivityId}: signalName={SignalName}")]
    private partial void LogSignalSubscriptionFailed(string activityId, string signalName, Exception exception);

    [LoggerMessage(EventId = 1030, Level = LogLevel.Information,
        Message = "Signal thrown: signalName={SignalName}, deliveredTo={DeliveredCount} subscribers")]
    private partial void LogSignalThrown(string signalName, int deliveredCount);

    [LoggerMessage(EventId = 1031, Level = LogLevel.Information, Message = "Signal delivered as boundary event for activity {ActivityId}")]
    private partial void LogSignalDeliveryBoundary(string activityId);

    [LoggerMessage(EventId = 1032, Level = LogLevel.Information, Message = "Signal delivered, completing activity {ActivityId}")]
    private partial void LogSignalDeliveryComplete(string activityId);

    [LoggerMessage(EventId = 1033, Level = LogLevel.Information,
        Message = "Event-based gateway: cancelled sibling {CancelledActivityId} because {WinningActivityId} completed first")]
    private partial void LogEventBasedGatewaySiblingCancelled(string cancelledActivityId, string winningActivityId);

    [LoggerMessage(EventId = 1037, Level = LogLevel.Information,
        Message = "Sub-process {ActivityId} initialized with child variable scope {ChildVariablesId}")]
    private partial void LogSubProcessInitialized(string activityId, Guid childVariablesId);

    [LoggerMessage(EventId = 1038, Level = LogLevel.Information,
        Message = "Sub-process {ActivityId} completed — all child activities done")]
    private partial void LogSubProcessCompleted(string activityId);

    [LoggerMessage(EventId = 1039, Level = LogLevel.Information,
        Message = "Scope child {ActivityId} cancelled (scope {ScopeId})")]
    private partial void LogScopeChildCancelled(string activityId, Guid scopeId);
}
