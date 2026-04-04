using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates.Services;
using Fleans.Domain.Effects;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Aggregates;

public record PendingActivity(Guid ActivityInstanceId, string ActivityId);

public record CompletedActivityTransitions(
    Guid ActivityInstanceId,
    string ActivityId,
    IReadOnlyList<ActivityTransition> Transitions);

public class WorkflowExecution
{
    private readonly WorkflowInstanceState _state;
    private readonly IWorkflowDefinition _definition;
    private readonly List<IDomainEvent> _uncommittedEvents = [];

    // Domain services
    private readonly UserTaskLifecycle _userTasks;
    private readonly MultiInstanceCoordinator _multiInstance;

    public WorkflowExecution(WorkflowInstanceState state, IWorkflowDefinition definition)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));

        _userTasks = new UserTaskLifecycle(state, Emit);
        _multiInstance = new MultiInstanceCoordinator(state, Emit);
    }

    /// <summary>
    /// Replay-mode constructor for event sourcing activation recovery.
    /// Only Apply methods run during replay — they never access _definition.
    /// </summary>
    internal WorkflowExecution(WorkflowInstanceState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _definition = null!;

        _userTasks = new UserTaskLifecycle(state, Emit);
        _multiInstance = new MultiInstanceCoordinator(state, Emit);
    }

    /// <summary>
    /// Replays a persisted event by calling Apply without adding to uncommitted events.
    /// Used during JournaledGrain activation to reconstruct state from the event log.
    /// </summary>
    internal void ReplayEvent(IDomainEvent @event) => Apply(@event);

    public IReadOnlyList<IDomainEvent> GetUncommittedEvents() => _uncommittedEvents.AsReadOnly();

    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();

    public void Start(string? startActivityId = null)
    {
        if (_state.IsStarted)
            throw new InvalidOperationException("Workflow is already started.");

        var instanceId = Guid.NewGuid();
        var rootVariablesId = Guid.NewGuid();

        Emit(new WorkflowStarted(instanceId, _definition.ProcessDefinitionId, rootVariablesId));

        Activity startActivity;
        if (startActivityId is not null)
        {
            startActivity = _definition.FindActivity(startActivityId)
                ?? throw new InvalidOperationException($"Activity '{startActivityId}' not found in workflow");
        }
        else
        {
            startActivity = _definition.GetStartActivity();
        }

        var variablesId = _state.GetRootVariablesId();

        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: startActivity.ActivityId,
            ActivityType: startActivity.GetType().Name,
            VariablesId: variablesId,
            ScopeId: null,
            MultiInstanceIndex: null,
            TokenId: null));
    }

    /// <summary>
    /// Marks the workflow as execution-started. Call after <see cref="Start"/>
    /// when you want a separate "initialize" vs "execution started" lifecycle.
    /// </summary>
    public void MarkExecutionStarted()
    {
        if (_state.IsStarted)
            return; // idempotent
        Emit(new ExecutionStarted());
    }

    public List<PendingActivity> GetPendingActivities()
    {
        return _state.GetActiveActivities()
            .Where(e => !e.IsExecuting)
            .Select(e => new PendingActivity(e.ActivityInstanceId, e.ActivityId))
            .ToList();
    }

    public void MarkExecuting(Guid activityInstanceId)
    {
        _ = _state.GetActiveEntry(activityInstanceId);
        Emit(new ActivityExecutionStarted(activityInstanceId));
    }

    public void MarkCompleted(Guid activityInstanceId, ExpandoObject variables)
    {
        var entry = _state.GetActiveEntry(activityInstanceId);
        Emit(new ActivityCompleted(activityInstanceId, entry.VariablesId, variables));
    }

    public void SetMultiInstanceTotal(Guid activityInstanceId, int total)
    {
        Emit(new MultiInstanceTotalSet(activityInstanceId, total));
    }

    public void ResolveTransitions(IReadOnlyList<CompletedActivityTransitions> completedTransitions)
    {
        foreach (var completed in completedTransitions)
        {
            var completedEntry = _state.GetEntry(completed.ActivityInstanceId);

            // Failed or cancelled entries don't transition
            if (completedEntry.ErrorCode is not null || completedEntry.IsCancelled)
                continue;

            foreach (var transition in completed.Transitions)
            {
                // Join gateway deduplication: if an active entry already exists
                // for this activity in the same scope, reset its executing flag
                if (transition.NextActivity.IsJoinGateway)
                {
                    var existingEntry = _state.GetActiveActivities()
                        .FirstOrDefault(e => e.ActivityId == transition.NextActivity.ActivityId
                            && e.ScopeId == completedEntry.ScopeId);
                    if (existingEntry is not null)
                    {
                        Emit(new ActivityExecutionReset(existingEntry.ActivityInstanceId));
                        continue;
                    }
                }

                Guid variablesId;
                Guid? tokenId;

                if (transition.Token == TokenAction.RestoreParent)
                {
                    // Fork-join: merge branch variable scopes, restore parent token
                    (tokenId, variablesId) = ResolveForkJoinTransition(completedEntry);
                }
                else
                {
                    // Determine variables ID (clone if needed)
                    if (transition.CloneVariables)
                    {
                        var newScopeId = Guid.NewGuid();
                        Emit(new VariableScopeCloned(newScopeId, completedEntry.VariablesId));
                        variablesId = newScopeId;
                    }
                    else
                    {
                        variablesId = completedEntry.VariablesId;
                    }

                    // Handle token propagation
                    tokenId = ResolveToken(transition, completedEntry);
                }

                // Spawn the next activity
                Emit(new ActivitySpawned(
                    ActivityInstanceId: Guid.NewGuid(),
                    ActivityId: transition.NextActivity.ActivityId,
                    ActivityType: transition.NextActivity.GetType().Name,
                    VariablesId: variablesId,
                    ScopeId: completedEntry.ScopeId,
                    MultiInstanceIndex: null,
                    TokenId: tokenId));
            }
        }
    }

    private Guid? ResolveToken(ActivityTransition transition, ActivityInstanceEntry sourceEntry)
    {
        switch (transition.Token)
        {
            case TokenAction.CreateNew:
                var newTokenId = Guid.NewGuid();

                // Create or find the fork state for this source activity
                var existingFork = _state.FindGatewayFork(sourceEntry.ActivityInstanceId);
                if (existingFork is null)
                {
                    Emit(new GatewayForkCreated(sourceEntry.ActivityInstanceId, sourceEntry.TokenId));
                }
                Emit(new GatewayForkTokenAdded(sourceEntry.ActivityInstanceId, newTokenId));

                return newTokenId;

            default: // TokenAction.Inherit
                return sourceEntry.TokenId;
        }
    }

    /// <summary>
    /// Handles fork-join variable merge and token restoration at a join gateway.
    /// Variables are merged in token creation order (the order branches were spawned at the fork).
    /// For conflicting variable names, the last branch in creation order wins.
    /// </summary>
    private (Guid? tokenId, Guid variablesId) ResolveForkJoinTransition(ActivityInstanceEntry sourceEntry)
    {
        if (!sourceEntry.TokenId.HasValue)
            return (null, sourceEntry.VariablesId);

        var fork = _state.FindForkByToken(sourceEntry.TokenId.Value);
        if (fork is null)
            return (sourceEntry.TokenId, sourceEntry.VariablesId);

        // Step 1: Read fork data BEFORE any Emit calls
        var forkEntry = _state.FindEntry(fork.ForkInstanceId);
        if (forkEntry is null)
        {
            // Fork entry not found — fall back to token restoration without merge
            var fallbackTokenId = fork.ConsumedTokenId ?? sourceEntry.TokenId;
            Emit(new GatewayForkRemoved(fork.ForkInstanceId));
            return (fallbackTokenId, sourceEntry.VariablesId);
        }
        var originalScopeId = forkEntry.VariablesId;

        // Step 2: Collect branch scopes in token creation order
        var branchScopeIds = new List<Guid>();
        foreach (var tokenId in fork.CreatedTokenIds)
        {
            var branchEntry = _state.Entries
                .FirstOrDefault(e => e.TokenId == tokenId && e.IsCompleted);
            if (branchEntry is not null && branchEntry.VariablesId != originalScopeId
                && !branchScopeIds.Contains(branchEntry.VariablesId))
            {
                branchScopeIds.Add(branchEntry.VariablesId);
            }
        }

        // Step 3: Merge each branch scope into the original scope (token creation order)
        foreach (var branchScopeId in branchScopeIds)
        {
            var branchVars = _state.GetVariableState(branchScopeId).Variables;
            Emit(new VariablesMerged(originalScopeId, branchVars));
        }

        // Step 4: Remove branch scopes
        if (branchScopeIds.Count > 0)
        {
            Emit(new VariableScopesRemoved(branchScopeIds));
        }

        // Step 5: Remove fork state and restore parent token
        var restoredTokenId = fork.ConsumedTokenId ?? sourceEntry.TokenId;
        Emit(new GatewayForkRemoved(fork.ForkInstanceId));

        return (restoredTokenId, originalScopeId);
    }

    public IReadOnlyList<IInfrastructureEffect> ProcessCommands(
        IReadOnlyList<IExecutionCommand> commands, Guid activityInstanceId)
    {
        var effects = new List<IInfrastructureEffect>();

        foreach (var command in commands)
        {
            switch (command)
            {
                case CompleteWorkflowCommand:
                    // Guard: only complete workflow when no other activities are active.
                    // The current EndEvent (identified by activityInstanceId) hasn't been
                    // marked completed yet — exclude it from the check.
                    var otherActive = _state.GetActiveActivities()
                        .Any(e => e.ActivityInstanceId != activityInstanceId);
                    if (otherActive)
                        break; // Defer — workflow will complete when last EndEvent fires

                    Emit(new WorkflowCompleted());
                    // If this is a child workflow, notify parent of completion
                    if (_state.ParentWorkflowInstanceId.HasValue)
                    {
                        var rootVariables = _state.GetMergedVariables(
                            _state.GetRootVariablesId());
                        effects.Add(new NotifyParentCompletedEffect(
                            _state.ParentWorkflowInstanceId.Value,
                            _state.ParentActivityId!,
                            rootVariables));
                    }
                    break;

                case SpawnActivityCommand spawn:
                    ProcessSpawnActivity(spawn, activityInstanceId);
                    break;

                case OpenSubProcessCommand openSub:
                    ProcessOpenSubProcess(openSub, activityInstanceId);
                    break;

                case RegisterTimerCommand timer:
                    effects.Add(new RegisterTimerEffect(
                        _state.Id, activityInstanceId,
                        timer.TimerActivityId, timer.DueTime));
                    break;

                case RegisterMessageCommand msg:
                    effects.Add(ProcessRegisterMessage(msg, activityInstanceId));
                    break;

                case RegisterSignalCommand signal:
                    effects.Add(new SubscribeSignalEffect(
                        signal.SignalName, _state.Id, signal.ActivityId, activityInstanceId));
                    break;

                case StartChildWorkflowCommand startChild:
                    effects.Add(ProcessStartChildWorkflow(startChild, activityInstanceId));
                    break;

                case AddConditionsCommand conditions:
                    effects.AddRange(ProcessAddConditions(conditions, activityInstanceId));
                    break;

                case RegisterUserTaskCommand regUserTask:
                    Emit(new UserTaskRegistered(
                        activityInstanceId, regUserTask.Assignee,
                        regUserTask.CandidateGroups, regUserTask.CandidateUsers,
                        regUserTask.ExpectedOutputVariables));
                    var currentEntry = _state.GetActiveEntry(activityInstanceId);
                    effects.Add(new RegisterUserTaskEffect(
                        _state.Id, activityInstanceId,
                        currentEntry.ActivityId, regUserTask.Assignee,
                        regUserTask.CandidateGroups, regUserTask.CandidateUsers,
                        regUserTask.ExpectedOutputVariables));
                    break;

                case ThrowSignalCommand throwSignal:
                    effects.Add(new ThrowSignalEffect(throwSignal.SignalName));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown execution command type: {command.GetType().Name}");
            }
        }

        return effects.AsReadOnly();
    }

    private void ProcessSpawnActivity(SpawnActivityCommand spawn, Guid activityInstanceId)
    {
        Guid variablesId;

        if (spawn.IsMultiInstanceIteration)
        {
            // Create a child variable scope for the iteration
            var newScopeId = Guid.NewGuid();
            Emit(new ChildVariableScopeCreated(newScopeId, spawn.ParentVariablesId!.Value));

            // Set loopCounter and optional item variable
            var iterVars = new ExpandoObject();
            var iterDict = (IDictionary<string, object?>)iterVars;
            iterDict["loopCounter"] = spawn.MultiInstanceIndex!.Value;

            if (spawn.IterationItemName is not null)
                iterDict[spawn.IterationItemName] = spawn.IterationItem;

            Emit(new VariablesMerged(newScopeId, iterVars));

            variablesId = newScopeId;
        }
        else
        {
            // Use the parent activity's variables scope
            var parentEntry = _state.GetActiveEntry(activityInstanceId);
            variablesId = parentEntry.VariablesId;
        }

        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: spawn.Activity.ActivityId,
            ActivityType: spawn.Activity.GetType().Name,
            VariablesId: variablesId,
            ScopeId: spawn.ScopeId,
            MultiInstanceIndex: spawn.MultiInstanceIndex,
            TokenId: null));
    }

    private void ProcessOpenSubProcess(OpenSubProcessCommand openSub, Guid hostActivityInstanceId)
    {
        var newScopeId = Guid.NewGuid();
        Emit(new ChildVariableScopeCreated(newScopeId, openSub.ParentVariablesId));

        var startEvent = openSub.SubProcess.Activities.OfType<StartEvent>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"SubProcess '{openSub.SubProcess.ActivityId}' does not contain a StartEvent.");

        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: startEvent.ActivityId,
            ActivityType: startEvent.GetType().Name,
            VariablesId: newScopeId,
            ScopeId: hostActivityInstanceId,
            MultiInstanceIndex: null,
            TokenId: null));
    }

    private SubscribeMessageEffect ProcessRegisterMessage(
        RegisterMessageCommand msg, Guid activityInstanceId)
    {
        var messageDef = _definition.GetMessageDefinition(msg.MessageDefinitionId);

        var correlationKey = string.Empty;
        if (messageDef.CorrelationKeyExpression is not null)
        {
            // Strip the "= " prefix from the expression to get the variable name
            var variableName = messageDef.CorrelationKeyExpression.StartsWith("= ")
                ? messageDef.CorrelationKeyExpression[2..]
                : messageDef.CorrelationKeyExpression;

            var correlationValue = _state.GetVariable(msg.VariablesId, variableName);
            correlationKey = correlationValue?.ToString()
                ?? throw new InvalidOperationException(
                    $"Correlation variable '{variableName}' is null for message '{messageDef.Name}'.");
        }

        return new SubscribeMessageEffect(
            messageDef.Name, correlationKey,
            _state.Id, msg.ActivityId, activityInstanceId);
    }

    private StartChildWorkflowEffect ProcessStartChildWorkflow(
        StartChildWorkflowCommand startChild, Guid activityInstanceId)
    {
        var callActivity = startChild.CallActivity;
        var entry = _state.GetActiveEntry(activityInstanceId);

        var childInstanceId = Guid.NewGuid();
        Emit(new ChildWorkflowLinked(activityInstanceId, childInstanceId));

        var parentVariables = _state.GetMergedVariables(entry.VariablesId);
        var inputVariables = callActivity.BuildChildInputVariables(parentVariables);

        return new StartChildWorkflowEffect(
            childInstanceId, callActivity.CalledProcessKey,
            inputVariables, callActivity.ActivityId);
    }

    private List<IInfrastructureEffect> ProcessAddConditions(
        AddConditionsCommand conditions, Guid activityInstanceId)
    {
        Emit(new ConditionSequencesAdded(activityInstanceId, conditions.SequenceFlowIds));

        var entry = _state.GetActiveEntry(activityInstanceId);
        var effects = new List<IInfrastructureEffect>();
        foreach (var evaluation in conditions.Evaluations)
        {
            var evalEvent = new EvaluateConditionEvent(
                _state.Id,
                _definition.WorkflowId,
                _definition.ProcessDefinitionId,
                activityInstanceId,
                entry.ActivityId,
                evaluation.SequenceFlowId,
                evaluation.Condition,
                entry.VariablesId);

            effects.Add(new PublishDomainEventEffect(evalEvent));
        }
        return effects;
    }

    // --- Event Handling (external event delivery) ---

    public IReadOnlyList<IInfrastructureEffect> HandleTimerFired(
        string timerActivityId, Guid hostActivityInstanceId)
    {
        // Stale guard: if entry is no longer active, ignore
        var entry = _state.FindEntry(hostActivityInstanceId);
        if (entry is null || entry.IsCompleted)
            return [];

        // Look up the activity type in the definition
        var activity = _definition.GetActivityAcrossScopes(timerActivityId);

        if (activity is BoundaryTimerEvent boundaryTimer)
        {
            return HandleBoundaryEventFired(
                boundaryTimer, boundaryTimer.AttachedToActivityId,
                boundaryTimer.IsInterrupting, entry, new ExpandoObject(),
                skipTimerActivityId: null,
                skipMessageName: null,
                skipSignalName: null);
        }

        // Intermediate catch timer: complete the activity with empty variables
        return CompleteActivity(timerActivityId, hostActivityInstanceId, new ExpandoObject());
    }

    public IReadOnlyList<IInfrastructureEffect> HandleMessageDelivery(
        string activityId, Guid hostActivityInstanceId, ExpandoObject variables)
    {
        // Stale guard: if entry is no longer active, ignore
        var entry = _state.FindEntry(hostActivityInstanceId);
        if (entry is null || entry.IsCompleted)
            return [];

        // Look up the activity type in the definition
        var activity = _definition.GetActivityAcrossScopes(activityId);

        if (activity is MessageBoundaryEvent boundaryMessage)
        {
            // For message boundaries, the fired message's subscription was already removed
            // by DeliverMessage. Skip it in unsubscribe to avoid deadlocks.
            var firedMessageDef = _definition.GetMessageDefinition(boundaryMessage.MessageDefinitionId);
            return HandleBoundaryEventFired(
                boundaryMessage, boundaryMessage.AttachedToActivityId,
                boundaryMessage.IsInterrupting, entry, variables,
                skipTimerActivityId: null,
                skipMessageName: firedMessageDef.Name,
                skipSignalName: null);
        }

        // Intermediate catch message: complete the activity with delivered variables
        return CompleteActivity(activityId, hostActivityInstanceId, variables);
    }

    public IReadOnlyList<IInfrastructureEffect> HandleSignalDelivery(
        string activityId, Guid hostActivityInstanceId)
    {
        // Stale guard: if entry is no longer active, ignore
        var entry = _state.FindEntry(hostActivityInstanceId);
        if (entry is null || entry.IsCompleted)
            return [];

        // Look up the activity type in the definition
        var activity = _definition.GetActivityAcrossScopes(activityId);

        if (activity is SignalBoundaryEvent boundarySignal)
        {
            // For signal boundaries, the fired signal's subscription was already removed.
            // Skip it in unsubscribe to avoid deadlocks.
            var firedSignalDef = _definition.GetSignalDefinition(boundarySignal.SignalDefinitionId);
            return HandleBoundaryEventFired(
                boundarySignal, boundarySignal.AttachedToActivityId,
                boundarySignal.IsInterrupting, entry, new ExpandoObject(),
                skipTimerActivityId: null,
                skipMessageName: null,
                skipSignalName: firedSignalDef.Name);
        }

        // Intermediate catch signal: complete the activity with empty variables
        return CompleteActivity(activityId, hostActivityInstanceId, new ExpandoObject());
    }

    // --- Scope Completion ---

    /// <summary>
    /// Detects when all entries in a subprocess or multi-instance scope are completed.
    /// Emits ActivityCompleted for completed scope hosts.
    /// Returns effects (e.g., boundary unsubscribe) and the list of completed host instance IDs
    /// so the grain can compute transitions for them via ResolveTransitions.
    /// </summary>
    public (IReadOnlyList<IInfrastructureEffect> Effects, IReadOnlyList<Guid> CompletedHostInstanceIds)
        CompleteFinishedSubProcessScopes()
    {
        var allEffects = new List<IInfrastructureEffect>();
        var allCompletedHostIds = new List<Guid>();

        const int maxIterations = 100;
        var iteration = 0;
        bool anyCompleted;

        do
        {
            if (++iteration > maxIterations)
                throw new InvalidOperationException(
                    "Sub-process completion loop exceeded max iterations — possible cycle in scope graph");

            anyCompleted = false;

            foreach (var entry in _state.GetActiveActivities().ToList())
            {
                var scopeDefinition = _definition.GetScopeForActivity(entry.ActivityId);
                var activity = scopeDefinition.GetActivity(entry.ActivityId);

                var isSubProcess = activity is SubProcess;
                var isMultiInstanceHost = activity is MultiInstanceActivity
                    && entry.MultiInstanceIndex is null;

                if (!isSubProcess && !isMultiInstanceHost) continue;

                var scopeEntries = _state.GetEntriesInScope(entry.ActivityInstanceId);

                if (scopeEntries.Count == 0 && !isMultiInstanceHost) continue;

                if (isMultiInstanceHost)
                {
                    var miResult = _multiInstance.TryComplete(
                        entry, (MultiInstanceActivity)activity, scopeEntries);
                    if (miResult.HostCompleted)
                    {
                        var boundaryEffects = BuildBoundaryUnsubscribeEffects(miResult.HostActivityId!, entry);
                        allEffects.AddRange(boundaryEffects);
                        allCompletedHostIds.Add(entry.ActivityInstanceId);
                        anyCompleted = true;
                    }
                    continue;
                }

                // SubProcess: all scope children must be completed
                if (!scopeEntries.All(e => e.IsCompleted)) continue;

                // If any scope child has an error (and wasn't handled by a boundary),
                // the subprocess should NOT auto-complete
                if (scopeEntries.Any(e => e.ErrorCode is not null)) continue;

                // All scope children are done — complete the sub-process host
                Emit(new ActivityCompleted(
                    entry.ActivityInstanceId, entry.VariablesId, new ExpandoObject()));

                var effects = BuildBoundaryUnsubscribeEffects(entry.ActivityId, entry);
                allEffects.AddRange(effects);
                allCompletedHostIds.Add(entry.ActivityInstanceId);
                anyCompleted = true;
            }
        } while (anyCompleted);

        return (allEffects.AsReadOnly(), allCompletedHostIds.AsReadOnly());
    }

    // --- User Task Handling ---

    public IReadOnlyList<IInfrastructureEffect> ClaimUserTask(
        Guid activityInstanceId, string userId)
    {
        return _userTasks.Claim(activityInstanceId, userId);
    }

    /// <summary>
    /// Unclaims a user task. No authorization check — any caller can unclaim any task.
    /// This is intentional for admin/support use cases.
    /// </summary>
    public IReadOnlyList<IInfrastructureEffect> UnclaimUserTask(Guid activityInstanceId)
    {
        return _userTasks.Unclaim(activityInstanceId);
    }

    public IReadOnlyList<IInfrastructureEffect> CompleteUserTask(
        Guid activityInstanceId, string userId, ExpandoObject variables)
    {
        var entry = _userTasks.ValidateAndPrepareCompletion(activityInstanceId, userId, variables);

        // Delegate to existing CompleteActivity — cleanup effects are handled
        // inside CompleteActivityInternal via BuildUserTaskCleanupEffects
        return CompleteActivityInternal(entry, variables);
    }

    private List<IInfrastructureEffect> BuildUserTaskCleanupEffects(Guid activityInstanceId)
    {
        if (_state.UserTasks.ContainsKey(activityInstanceId))
        {
            Emit(new UserTaskUnregistered(activityInstanceId));
            return [new CompleteUserTaskPersistenceEffect(activityInstanceId)];
        }
        return [];
    }

    // --- Condition Sequence Handling ---

    public void EvaluateConditionSequence(Guid activityInstanceId, string sequenceId, bool result)
    {
        Emit(new ConditionSequenceEvaluated(activityInstanceId, sequenceId, result));
    }

    public void CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        var entry = _state.GetFirstActive(activityId)
            ?? throw new InvalidOperationException(
                $"No active entry found for activity '{activityId}'.");

        Emit(new ConditionSequenceEvaluated(entry.ActivityInstanceId, conditionSequenceId, result));

        var gateway = _definition.GetActivityAcrossScopes(activityId) as ConditionalGateway
            ?? throw new InvalidOperationException(
                $"Activity '{activityId}' is not a ConditionalGateway.");

        bool isDecisionMade = IsGatewayDecisionMade(activityId, entry.ActivityInstanceId, gateway, result);

        if (isDecisionMade)
        {
            Emit(new ActivityCompleted(entry.ActivityInstanceId, entry.VariablesId, new ExpandoObject()));
        }
    }

    private bool IsGatewayDecisionMade(
        string activityId, Guid activityInstanceId, ConditionalGateway gateway, bool result)
    {
        if (gateway is InclusiveGateway)
        {
            // InclusiveGateway: wait for ALL conditions to be evaluated, never short-circuit
            var sequences = _state.GetConditionSequenceStatesForGateway(activityInstanceId);
            if (!sequences.All(s => s.IsEvaluated))
                return false;

            if (sequences.Any(s => s.Result))
                return true;

            // All false — need default flow
            return EnsureDefaultFlowExists(activityId, "InclusiveGateway");
        }
        else
        {
            // ExclusiveGateway: short-circuit on first true condition
            if (result)
                return true;

            var sequences = _state.GetConditionSequenceStatesForGateway(activityInstanceId);
            if (!sequences.All(s => s.IsEvaluated))
                return false;

            // All evaluated, all false — need default flow
            return EnsureDefaultFlowExists(activityId, "Gateway");
        }
    }

    private bool EnsureDefaultFlowExists(string activityId, string gatewayType)
    {
        var hasDefault = _definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .Any(sf => sf.Source.ActivityId == activityId);

        if (!hasDefault)
            throw new InvalidOperationException(
                $"{gatewayType} {activityId}: all conditions evaluated to false and no default flow exists");

        return true;
    }

    // --- Child Workflow Handling ---

    public IReadOnlyList<IInfrastructureEffect> OnChildWorkflowCompleted(
        string parentActivityId, ExpandoObject childVariables)
    {
        // Stale guard: if the call activity is no longer active, ignore
        var entry = _state.GetFirstActive(parentActivityId);
        if (entry is null)
            return [];

        var callActivity = _definition.GetActivityAcrossScopes(parentActivityId) as CallActivity
            ?? throw new InvalidOperationException(
                $"Activity '{parentActivityId}' is not a CallActivity.");

        var mappedOutput = callActivity.BuildParentOutputVariables(childVariables);
        return CompleteActivity(parentActivityId, entry.ActivityInstanceId, mappedOutput);
    }

    public IReadOnlyList<IInfrastructureEffect> OnChildWorkflowFailed(
        string parentActivityId, Exception exception)
    {
        // Stale guard: if the call activity is no longer active, ignore
        var entry = _state.GetFirstActive(parentActivityId);
        if (entry is null)
            return [];

        return FailActivity(parentActivityId, entry.ActivityInstanceId, exception);
    }

    // --- Parent Info ---

    public void SetParentInfo(Guid parentInstanceId, string parentActivityId)
    {
        Emit(new ParentInfoSet(parentInstanceId, parentActivityId));
    }

    public void MergeVariables(Guid variablesId, ExpandoObject variables)
    {
        Emit(new VariablesMerged(variablesId, variables));
    }

    // --- Activity Lifecycle (external entry points) ---

    public IReadOnlyList<IInfrastructureEffect> CompleteActivity(
        string activityId, Guid? activityInstanceId, ExpandoObject variables)
    {
        var entry = ResolveEntry(activityId, activityInstanceId);

        // Stale guard: if entry is already completed, ignore stale callbacks
        if (entry.IsCompleted)
            return [];

        // User task guard: force callers through CompleteUserTask
        if (_state.UserTasks.ContainsKey(entry.ActivityInstanceId))
            throw new InvalidOperationException(
                $"Activity '{activityId}' is a user task. " +
                $"Use CompleteUserTask instead of CompleteActivity.");

        return CompleteActivityInternal(entry, variables);
    }

    private IReadOnlyList<IInfrastructureEffect> CompleteActivityInternal(
        ActivityInstanceEntry entry, ExpandoObject variables)
    {
        Emit(new ActivityCompleted(entry.ActivityInstanceId, entry.VariablesId, variables));

        var effects = new List<IInfrastructureEffect>();

        // Unsubscribe boundary subscriptions on this activity
        effects.AddRange(BuildBoundaryUnsubscribeEffects(entry.ActivityId, entry));

        // Cancel event-based gateway siblings
        effects.AddRange(CancelEventBasedGatewaySiblings(entry.ActivityId, entry));

        // Clean up user task registry if applicable
        effects.AddRange(BuildUserTaskCleanupEffects(entry.ActivityInstanceId));

        return effects.AsReadOnly();
    }

    public IReadOnlyList<IInfrastructureEffect> FailActivity(
        string activityId, Guid? activityInstanceId, Exception exception)
    {
        var entry = ResolveEntry(activityId, activityInstanceId);

        // Stale guard: if entry is already completed, ignore stale callbacks
        if (entry.IsCompleted)
            return [];

        // Extract error code from exception type
        int errorCode;
        string errorMessage;
        if (exception is ActivityException activityException)
        {
            var errorState = activityException.GetActivityErrorState();
            errorCode = errorState.Code;
            errorMessage = errorState.Message;
        }
        else
        {
            errorCode = 500;
            errorMessage = exception.Message;
        }

        Emit(new ActivityFailed(entry.ActivityInstanceId, errorCode, errorMessage));

        var effects = new List<IInfrastructureEffect>();

        // Clean up user task registry if applicable
        effects.AddRange(BuildUserTaskCleanupEffects(entry.ActivityInstanceId));

        // Search for boundary error handler
        var boundaryHandler = _definition.FindBoundaryErrorHandler(activityId, errorCode.ToString());

        if (boundaryHandler is not null)
        {
            var (boundaryEvent, scope, attachedToActivityId) = boundaryHandler.Value;

            // Find the attached-to entry (may differ from the failed activity if
            // the boundary is on a parent subprocess)
            var attachedEntry = _state.GetFirstActive(attachedToActivityId);

            var scopeIdForCancel = attachedEntry?.ActivityInstanceId ?? entry.ActivityInstanceId;

            // Cancel scope children of the attached-to activity
            effects.AddRange(CancelScopeChildren(scopeIdForCancel));

            // Cancel the attached-to entry itself (e.g., the SubProcess)
            // so that CompleteFinishedSubProcessScopes doesn't auto-complete it
            if (attachedEntry is not null && !attachedEntry.IsCompleted)
            {
                Emit(new ActivityCancelled(
                    attachedEntry.ActivityInstanceId,
                    $"Interrupted by boundary error event '{boundaryEvent.ActivityId}'"));
                effects.AddRange(BuildUserTaskCleanupEffects(attachedEntry.ActivityInstanceId));
            }

            // Unsubscribe all boundary subscriptions on the attached activity
            effects.AddRange(BuildBoundaryUnsubscribeEffects(attachedToActivityId, attachedEntry ?? entry));

            // Spawn the boundary error event activity
            Emit(new ActivitySpawned(
                ActivityInstanceId: Guid.NewGuid(),
                ActivityId: boundaryEvent.ActivityId,
                ActivityType: boundaryEvent.GetType().Name,
                VariablesId: entry.VariablesId,
                ScopeId: attachedEntry?.ScopeId ?? entry.ScopeId,
                MultiInstanceIndex: null,
                TokenId: null));
        }
        else
        {
            // No boundary handler found

            // If this is a multi-instance iteration, cancel siblings and fail host
            if (entry.MultiInstanceIndex is not null && entry.ScopeId.HasValue)
            {
                // Cancel all active sibling iterations
                effects.AddRange(CancelScopeChildren(entry.ScopeId.Value));

                // Service handles scope cleanup and host failure
                var miResult = _multiInstance.FailHost(entry, errorCode, errorMessage);

                // Aggregate builds shared cleanup effects
                effects.AddRange(BuildUserTaskCleanupEffects(miResult.HostInstanceId));
            }

            // If this is a child workflow with no remaining active activities, notify parent
            if (_state.ParentWorkflowInstanceId.HasValue
                && !_state.GetActiveActivities().Any())
            {
                effects.Add(new NotifyParentFailedEffect(
                    _state.ParentWorkflowInstanceId.Value,
                    _state.ParentActivityId!,
                    exception));
            }
        }

        return effects.AsReadOnly();
    }

    // --- Activity Lifecycle Helpers ---

    private ActivityInstanceEntry ResolveEntry(string activityId, Guid? activityInstanceId)
    {
        if (activityInstanceId.HasValue)
        {
            return _state.GetEntry(activityInstanceId.Value);
        }

        return _state.GetFirstActive(activityId)
            ?? throw new InvalidOperationException(
                $"No active entry found for activity '{activityId}'.");
    }

    // --- Boundary Event Handling ---

    /// <summary>
    /// Unified handler for boundary timer, message, and signal events firing.
    /// Handles both interrupting (cancel attached + scope children + unsubscribe siblings)
    /// and non-interrupting (clone variables, leave attached running) modes.
    /// </summary>
    private IReadOnlyList<IInfrastructureEffect> HandleBoundaryEventFired(
        Activity boundaryActivity,
        string attachedToActivityId,
        bool isInterrupting,
        ActivityInstanceEntry hostEntry,
        ExpandoObject deliveredVariables,
        string? skipTimerActivityId,
        string? skipMessageName,
        string? skipSignalName)
    {
        var effects = new List<IInfrastructureEffect>();

        Guid variablesId;
        Guid? scopeId;

        if (isInterrupting)
        {
            // Cancel the attached activity
            Emit(new ActivityCancelled(
                hostEntry.ActivityInstanceId,
                $"Interrupted by boundary event '{boundaryActivity.ActivityId}'"));
            effects.AddRange(BuildUserTaskCleanupEffects(hostEntry.ActivityInstanceId));

            // Recursively cancel scope children
            effects.AddRange(CancelScopeChildren(hostEntry.ActivityInstanceId));

            // Build unsubscribe effects for OTHER boundary subscriptions
            // (skip the one that fired to avoid deadlocks)
            effects.AddRange(BuildBoundaryUnsubscribeEffects(
                attachedToActivityId, hostEntry,
                skipTimerActivityId, skipMessageName, skipSignalName));

            // Use the attached activity's variables scope
            variablesId = hostEntry.VariablesId;
            scopeId = hostEntry.ScopeId;
        }
        else
        {
            // Non-interrupting: leave attached activity running, clone variables
            var clonedScopeId = Guid.NewGuid();
            Emit(new VariableScopeCloned(clonedScopeId, hostEntry.VariablesId));
            variablesId = clonedScopeId;
            scopeId = hostEntry.ScopeId;

            // Merge delivered variables into cloned scope
            if (((IDictionary<string, object?>)deliveredVariables).Count > 0)
            {
                Emit(new VariablesMerged(clonedScopeId, deliveredVariables));
            }

            // For non-interrupting cycle timers, re-register the timer with decremented count
            if (boundaryActivity is BoundaryTimerEvent boundaryTimer
                && boundaryTimer.TimerDefinition.Type == TimerType.Cycle)
            {
                // Use tracked cycle state if available; otherwise use original definition for first fire
                var currentCycle = _state.GetTimerCycleState(
                    hostEntry.ActivityInstanceId, boundaryTimer.ActivityId)
                    ?? boundaryTimer.TimerDefinition;

                var nextCycle = currentCycle.DecrementCycle();
                Emit(new TimerCycleUpdated(
                    hostEntry.ActivityInstanceId, boundaryTimer.ActivityId, nextCycle));

                if (nextCycle is not null)
                {
                    effects.Add(new RegisterTimerEffect(
                        _state.Id, hostEntry.ActivityInstanceId,
                        boundaryTimer.ActivityId, nextCycle.GetDueTime()));
                }
            }
        }

        // Spawn the boundary event activity
        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: boundaryActivity.ActivityId,
            ActivityType: boundaryActivity.GetType().Name,
            VariablesId: variablesId,
            ScopeId: scopeId,
            MultiInstanceIndex: null,
            TokenId: null));

        return effects.AsReadOnly();
    }

    /// <summary>
    /// Recursively cancels all active entries within a scope (and their nested scope children).
    /// </summary>
    private List<IInfrastructureEffect> CancelScopeChildren(Guid scopeId)
    {
        var effects = new List<IInfrastructureEffect>();
        foreach (var entry in _state.GetActiveActivities()
            .Where(e => e.ScopeId == scopeId).ToList())
        {
            // Recursively cancel nested scope children first
            if (_state.HasActiveChildrenInScope(entry.ActivityInstanceId))
                effects.AddRange(CancelScopeChildren(entry.ActivityInstanceId));

            Emit(new ActivityCancelled(
                entry.ActivityInstanceId,
                "Scope cancelled by boundary event"));

            // Clean up user task registry if this is a user task
            effects.AddRange(BuildUserTaskCleanupEffects(entry.ActivityInstanceId));
        }
        return effects;
    }

    // --- Boundary Unsubscribe Helpers ---

    private List<IInfrastructureEffect> BuildBoundaryUnsubscribeEffects(
        string activityId, ActivityInstanceEntry hostEntry)
    {
        var effects = new List<IInfrastructureEffect>();
        var scope = _definition.FindScopeForActivity(activityId);
        if (scope is null) return effects;

        // Boundary timer events
        foreach (var boundaryTimer in scope.GetBoundaryTimerEvents(activityId))
        {
            effects.Add(new UnregisterTimerEffect(
                _state.Id, hostEntry.ActivityInstanceId, boundaryTimer.ActivityId));
        }

        // Boundary message events
        foreach (var boundaryMsg in scope.GetBoundaryMessageEvents(activityId))
        {
            var messageDef = _definition.GetMessageDefinition(boundaryMsg.MessageDefinitionId);
            var correlationKey = ResolveCorrelationKey(messageDef, hostEntry.VariablesId);
            effects.Add(new UnsubscribeMessageEffect(messageDef.Name, correlationKey));
        }

        // Boundary signal events
        foreach (var boundarySig in scope.GetBoundarySignalEvents(activityId))
        {
            var signalDef = _definition.GetSignalDefinition(boundarySig.SignalDefinitionId);
            effects.Add(new UnsubscribeSignalEffect(signalDef.Name, _state.Id, boundarySig.ActivityId));
        }

        return effects;
    }

    /// <summary>
    /// Builds unsubscribe effects for boundary subscriptions, with optional skip parameters
    /// to avoid unsubscribing the boundary that just fired (which could cause deadlocks).
    /// </summary>
    private List<IInfrastructureEffect> BuildBoundaryUnsubscribeEffects(
        string activityId, ActivityInstanceEntry hostEntry,
        string? skipTimerActivityId, string? skipMessageName, string? skipSignalName)
    {
        var effects = new List<IInfrastructureEffect>();
        var scope = _definition.FindScopeForActivity(activityId);
        if (scope is null) return effects;

        // Boundary timer events (skip the fired timer — it already fired)
        foreach (var boundaryTimer in scope.GetBoundaryTimerEvents(activityId)
            .Where(b => b.ActivityId != skipTimerActivityId))
        {
            effects.Add(new UnregisterTimerEffect(
                _state.Id, hostEntry.ActivityInstanceId, boundaryTimer.ActivityId));
        }

        // Boundary message events (skip the fired message — subscription already removed)
        foreach (var boundaryMsg in scope.GetBoundaryMessageEvents(activityId))
        {
            var messageDef = _definition.GetMessageDefinition(boundaryMsg.MessageDefinitionId);
            if (messageDef.Name == skipMessageName) continue;
            var correlationKey = ResolveCorrelationKey(messageDef, hostEntry.VariablesId);
            effects.Add(new UnsubscribeMessageEffect(messageDef.Name, correlationKey));
        }

        // Boundary signal events (skip the fired signal — subscription already removed)
        foreach (var boundarySig in scope.GetBoundarySignalEvents(activityId))
        {
            var signalDef = _definition.GetSignalDefinition(boundarySig.SignalDefinitionId);
            if (signalDef.Name == skipSignalName) continue;
            effects.Add(new UnsubscribeSignalEffect(signalDef.Name, _state.Id, boundarySig.ActivityId));
        }

        return effects;
    }

    private List<IInfrastructureEffect> CancelEventBasedGatewaySiblings(
        string completedActivityId, ActivityInstanceEntry completedEntry)
    {
        var effects = new List<IInfrastructureEffect>();
        var siblings = _definition.GetEventBasedGatewaySiblings(completedActivityId);

        if (siblings.Count == 0) return effects;

        foreach (var siblingId in siblings)
        {
            var siblingEntry = _state.GetFirstActive(siblingId);
            if (siblingEntry is null) continue;

            Emit(new ActivityCancelled(
                siblingEntry.ActivityInstanceId,
                "Event-based gateway: sibling event completed"));

            // Build unsubscribe effects based on sibling type
            var siblingActivity = _definition.GetActivityAcrossScopes(siblingId);
            switch (siblingActivity)
            {
                case TimerIntermediateCatchEvent:
                    effects.Add(new UnregisterTimerEffect(
                        _state.Id, siblingEntry.ActivityInstanceId, siblingId));
                    break;

                case MessageIntermediateCatchEvent msgCatch:
                    var messageDef = _definition.GetMessageDefinition(msgCatch.MessageDefinitionId);
                    var correlationKey = ResolveCorrelationKey(messageDef, completedEntry.VariablesId);
                    effects.Add(new UnsubscribeMessageEffect(messageDef.Name, correlationKey));
                    break;

                case SignalIntermediateCatchEvent sigCatch:
                    var signalDef = _definition.GetSignalDefinition(sigCatch.SignalDefinitionId);
                    effects.Add(new UnsubscribeSignalEffect(signalDef.Name, _state.Id, siblingId));
                    break;
            }
        }

        return effects;
    }

    private string ResolveCorrelationKey(MessageDefinition messageDef, Guid variablesId)
    {
        if (messageDef.CorrelationKeyExpression is null)
            return string.Empty;

        var variableName = messageDef.CorrelationKeyExpression.StartsWith("= ")
            ? messageDef.CorrelationKeyExpression[2..]
            : messageDef.CorrelationKeyExpression;

        var correlationValue = _state.GetVariable(variablesId, variableName);
        return correlationValue?.ToString()
            ?? throw new InvalidOperationException(
                $"Correlation variable '{variableName}' is null for message '{messageDef.Name}'.");
    }

    // --- Emit / Apply pattern ---

    private void Emit(IDomainEvent @event)
    {
        // Snapshot mutable ExpandoObject data to preserve event immutability
        var snapshotted = @event switch
        {
            ActivityCompleted e => e with { Variables = SnapshotExpandoObject(e.Variables) },
            VariablesMerged e => e with { Variables = SnapshotExpandoObject(e.Variables) },
            _ => @event
        };

        Apply(snapshotted);
        _uncommittedEvents.Add(snapshotted);
    }

    private static ExpandoObject SnapshotExpandoObject(ExpandoObject source)
    {
        var snapshot = new ExpandoObject();
        var target = (IDictionary<string, object?>)snapshot;
        foreach (var kvp in (IDictionary<string, object?>)source)
            target[kvp.Key] = kvp.Value;
        return snapshot;
    }

    private void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case WorkflowStarted e:
                ApplyWorkflowStarted(e);
                break;
            case ExecutionStarted:
                _state.Start();
                break;
            case WorkflowCompleted:
                _state.Complete();
                break;
            case ActivitySpawned e:
                ApplyActivitySpawned(e);
                break;
            case ActivityExecutionStarted e:
                ApplyActivityExecutionStarted(e);
                break;
            case ActivityCompleted e:
                ApplyActivityCompleted(e);
                break;
            case ActivityFailed e:
                ApplyActivityFailed(e);
                break;
            case ActivityExecutionReset e:
                _state.GetActiveEntry(e.ActivityInstanceId).ResetExecuting();
                break;
            case ActivityCancelled e:
                ApplyActivityCancelled(e);
                break;
            case MultiInstanceTotalSet e:
                _state.GetActiveEntry(e.ActivityInstanceId).SetMultiInstanceTotal(e.Total);
                break;
            case VariablesMerged e:
                _state.MergeState(e.VariablesId, e.Variables);
                break;

            case ChildVariableScopeCreated e:
                _state.AddChildVariableState(e.ScopeId, e.ParentScopeId);
                break;
            case VariableScopeCloned e:
                _state.AddCloneOfVariableState(e.NewScopeId, e.SourceScopeId);
                break;
            case VariableScopesRemoved e:
                _state.RemoveVariableStates(e.ScopeIds);
                break;
            case ConditionSequencesAdded e:
                _state.AddConditionSequenceStates(e.GatewayInstanceId, e.SequenceFlowIds);
                break;
            case ConditionSequenceEvaluated e:
                _state.SetConditionSequenceResult(e.GatewayInstanceId, e.SequenceFlowId, e.Result);
                break;
            case GatewayForkCreated e:
                _state.CreateGatewayFork(e.ForkInstanceId, e.ConsumedTokenId);
                break;
            case GatewayForkTokenAdded e:
                ApplyGatewayForkTokenAdded(e);
                break;
            case GatewayForkRemoved e:
                _state.RemoveGatewayFork(e.ForkInstanceId);
                break;
            case ParentInfoSet e:
                _state.SetParentInfo(e.ParentInstanceId, e.ParentActivityId);
                break;
            case ChildWorkflowLinked e:
                _state.GetActiveEntry(e.ActivityInstanceId).SetChildWorkflowInstanceId(e.ChildWorkflowInstanceId);
                break;
            case UserTaskRegistered e:
                var meta = new UserTaskMetadata();
                meta.Initialize(e.Assignee, e.CandidateGroups, e.CandidateUsers, e.ExpectedOutputVariables);
                _state.UserTasks[e.ActivityInstanceId] = meta;
                break;
            case UserTaskClaimed e:
                _state.UserTasks[e.ActivityInstanceId].Claim(e.UserId, e.ClaimedAt);
                break;
            case UserTaskUnclaimed e:
                _state.UserTasks[e.ActivityInstanceId].Unclaim();
                break;
            case UserTaskUnregistered e:
                _state.UserTasks.Remove(e.ActivityInstanceId);
                break;
            case TimerCycleUpdated e:
                _state.SetTimerCycleState(e.HostActivityInstanceId, e.TimerActivityId, e.RemainingCycle);
                break;
            default:
                throw new InvalidOperationException($"Unknown domain event type: {@event.GetType().Name}");
        }
    }

    private void ApplyWorkflowStarted(WorkflowStarted e)
    {
        _state.Initialize(e.InstanceId, e.ProcessDefinitionId, e.RootVariablesId);
    }

    private void ApplyActivitySpawned(ActivitySpawned e)
    {
        var entry = new ActivityInstanceEntry(
            e.ActivityInstanceId,
            e.ActivityId,
            _state.Id,
            e.ScopeId);

        entry.SetActivityType(e.ActivityType);
        entry.SetVariablesId(e.VariablesId);

        if (e.TokenId.HasValue)
            entry.SetTokenId(e.TokenId.Value);

        if (e.MultiInstanceIndex.HasValue)
            entry.SetMultiInstanceIndex(e.MultiInstanceIndex.Value);

        _state.AddEntries([entry]);
    }

    private void ApplyActivityExecutionStarted(ActivityExecutionStarted e)
    {
        var entry = _state.GetActiveEntry(e.ActivityInstanceId);
        entry.Execute();
    }

    private void ApplyActivityCompleted(ActivityCompleted e)
    {
        var entry = _state.GetActiveEntry(e.ActivityInstanceId);
        entry.Complete();
        _state.MergeState(e.VariablesId, e.Variables);
    }

    private void ApplyActivityFailed(ActivityFailed e)
    {
        var entry = _state.GetActiveEntry(e.ActivityInstanceId);
        entry.Fail(e.ErrorCode, e.ErrorMessage);
    }

    private void ApplyActivityCancelled(ActivityCancelled e)
    {
        var entry = _state.GetActiveEntry(e.ActivityInstanceId);
        entry.Cancel(e.Reason);
    }

    private void ApplyGatewayForkTokenAdded(GatewayForkTokenAdded e)
    {
        var fork = _state.GetGatewayFork(e.ForkInstanceId);
        fork.CreatedTokenIds.Add(e.TokenId);
    }
}
