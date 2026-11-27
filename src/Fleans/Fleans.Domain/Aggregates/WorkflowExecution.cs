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

public partial class WorkflowExecution
{
    private readonly WorkflowInstanceState _state;
    private readonly IWorkflowDefinition _definition;
    private readonly List<WorkflowEvent> _uncommittedEvents = [];

    // Domain services
    private readonly UserTaskLifecycle _userTasks;
    private readonly MultiInstanceCoordinator _multiInstance;

    /// <summary>Evaluates <c>fleans:ioMapping</c> sources; required only by definitions that have mappings.</summary>
    private readonly IMappingExpressionEvaluator? _mappingEvaluator;

    public WorkflowExecution(
        WorkflowInstanceState state, IWorkflowDefinition definition, IMappingExpressionEvaluator? mappingEvaluator = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _mappingEvaluator = mappingEvaluator;

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
    internal void ReplayEvent(WorkflowEvent @event) => Apply(@event);

    public IReadOnlyList<WorkflowEvent> GetUncommittedEvents() => _uncommittedEvents.AsReadOnly();

    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();

    /// <param name="instanceId">
    /// The instance's public id — the workflow instance grain key. It becomes <c>State.Id</c>, which
    /// effects (message/signal subscriptions, timers) and logs hand to other grains, so it must be
    /// the id those grains can address. Domain tests may omit it.
    /// </param>
    public void Start(string? startActivityId = null, string? businessId = null, Guid? instanceId = null)
    {
        if (_state.IsStarted)
            throw new InvalidOperationException("Workflow is already started.");

        // All validation runs before the first Emit: a rejected start leaves no state behind.
        BusinessIdRules.Validate(businessId);

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

        var rootVariablesId = Guid.NewGuid();

        Emit(new WorkflowStarted(instanceId ?? Guid.NewGuid(), _definition.ProcessDefinitionId, rootVariablesId, BusinessId: businessId));

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
    /// Marks the workflow as execution-started and returns the root-scope entry
    /// effects (e.g. event sub-process timer/message listener registrations) that
    /// must be performed by the grain before the first execution tick. Returning
    /// the effects from here keeps the aggregate as the single owner of "what
    /// happens at scope entry" — the grain never reaches into the aggregate to
    /// assemble effects in a specific order.
    /// </summary>
    public IReadOnlyList<IInfrastructureEffect> MarkExecutionStarted()
    {
        if (_state.IsStarted)
            return Array.Empty<IInfrastructureEffect>(); // idempotent
        Emit(new ExecutionStarted());
        return BuildRootScopeEntryEffects();
    }

    /// <summary>
    /// Creates and starts the instance in one transition (#863): <see cref="Start"/>'s
    /// <c>WorkflowStarted</c> and start activity, the parent link of a child instance, the input
    /// variables, then <c>ExecutionStarted</c>. The grain journals them as one batch, so an instance
    /// is either absent or started — never created and left unstarted. Validation runs before
    /// the first emit, so a rejected start leaves no state behind. Returns the root-scope entry
    /// effects, as <see cref="MarkExecutionStarted"/> does.
    /// </summary>
    public IReadOnlyList<IInfrastructureEffect> CreateAndStart(
        Guid instanceId,
        string? startActivityId = null,
        string? businessId = null,
        ExpandoObject? variables = null,
        ParentInfo? parent = null)
    {
        Start(startActivityId, businessId, instanceId);

        if (parent is not null)
            Emit(new ParentInfoSet(parent.InstanceId, parent.ActivityId));

        if (variables is not null && ((IDictionary<string, object?>)variables).Count > 0)
            MergeVariables(_state.GetRootVariablesId(), variables);

        return MarkExecutionStarted();
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

    /// <summary>
    /// Completes an activity that finished inside the execution loop. Returns the failure's
    /// effects when its output mappings fail to evaluate (the activity is then failed, not completed).
    /// An entry that is not in the state any more was finished and pruned (#1050): it is stale, like
    /// a cancelled one, so this does nothing. The grain logs it.
    /// </summary>
    public IReadOnlyList<IInfrastructureEffect> MarkCompleted(Guid activityInstanceId, ExpandoObject variables)
    {
        if (_state.FindEntry(activityInstanceId) is not { } entry)
            return [];
        // Escalation boundary cancellation race: when an interrupting escalation
        // boundary fires, the host SubProcess/CallActivity is cancelled immediately.
        // However, the grain's RunExecutionLoop may still call MarkCompleted for
        // activities within that cancelled scope. Using GetEntry (not GetActiveEntry)
        // and returning early for cancelled entries prevents a spurious throw.
        // This guard is intentionally narrow — only IsCancelled returns early.
        // Failed or already-completed entries fall through to the checks below.
        if (entry.IsCancelled) return [];
        // A failed entry must not be silently completed.
        if (entry.ErrorCode is not null)
            throw new InvalidOperationException(
                $"Activity '{entry.ActivityId}' has failed — cannot mark completed.");
        // For any other terminal state, delegate to GetActiveEntry which throws —
        // a double-complete is a bug, not a race.
        if (entry.IsCompleted)
            _ = _state.GetActiveEntry(activityInstanceId); // throws

        ExpandoObject outputs;
        try
        {
            outputs = MapOutputs(entry, variables, entry.ExecutionVariablesId);
        }
        catch (MappingEvaluationException ex)
        {
            return FailActivity(entry.ActivityId, entry.ActivityInstanceId, ex);
        }

        Emit(new ActivityCompleted(activityInstanceId, entry.VariablesId, outputs));
        RemoveInputScope(entry);
        return [];
    }

    // --- fleans:ioMapping ---

    /// <summary>
    /// Evaluates the activity's input mappings against its scope into a new local scope it then
    /// executes against (<see cref="ActivityInstanceEntry.ExecutionVariablesId"/>). Each input also
    /// sees the inputs before it. Called once, before the activity executes; an activity without
    /// input mappings, or already mapped, is left alone. When an input fails to evaluate the activity
    /// is failed instead: <c>Failed</c> is true and the effects are the failure's.
    /// </summary>
    public (bool Failed, IReadOnlyList<IInfrastructureEffect> Effects) MapInputs(Guid activityInstanceId)
    {
        var entry = _state.GetActiveEntry(activityInstanceId);
        if (entry.InputScopeId is not null)
            return (false, []);

        var activity = FindMappedActivity(entry);
        if (activity is null || activity.InputMappings.Count == 0)
            return (false, []);

        var context = _state.GetMergedVariables(entry.VariablesId);
        var contextDict = (IDictionary<string, object?>)context;
        var inputs = new ExpandoObject();
        var inputsDict = (IDictionary<string, object?>)inputs;
        try
        {
            foreach (var input in activity.InputMappings)
            {
                var value = EvaluateMapping("Input", input.Source, input.Target, entry, context);
                inputsDict[input.Target] = value;
                contextDict[input.Target] = value;
            }
        }
        catch (MappingEvaluationException ex)
        {
            return (true, FailActivity(entry.ActivityId, entry.ActivityInstanceId, ex));
        }

        Emit(new ActivityInputScopeOpened(activityInstanceId, Guid.NewGuid(), inputs));
        return (false, []);
    }

    /// <summary>
    /// The variables a completing activity hands to its enclosing scope. Without output mappings
    /// that is its local scope (a boundary event's delivered payload) overlaid with
    /// <paramref name="result"/>, minus its local inputs; with them, only their values, evaluated
    /// against scope <paramref name="evaluationScopeId"/> overlaid with <paramref name="result"/>.
    /// </summary>
    /// <exception cref="MappingEvaluationException">An output did not evaluate.</exception>
    private ExpandoObject MapOutputs(ActivityInstanceEntry entry, ExpandoObject result, Guid evaluationScopeId)
    {
        var activity = FindMappedActivity(entry);
        if (activity is null)
            return result;
        if (activity.OutputMappings.Count == 0)
            return WithoutLocalInputs(activity, WithLocalScope(entry, result));

        var context = _state.GetMergedVariables(evaluationScopeId);
        var contextDict = (IDictionary<string, object?>)context;
        foreach (var (key, value) in (IDictionary<string, object?>)result)
            contextDict[key] = value;

        var outputs = new ExpandoObject();
        var outputsDict = (IDictionary<string, object?>)outputs;
        foreach (var output in activity.OutputMappings)
            outputsDict[output.Target] = EvaluateMapping("Output", output.Source, output.Target, entry, context);
        return outputs;
    }

    /// <summary>
    /// <paramref name="result"/> laid over the entry's local scope, so variables delivered into that
    /// scope pass through; <paramref name="result"/> itself when the entry has no local scope.
    /// </summary>
    private ExpandoObject WithLocalScope(ActivityInstanceEntry entry, ExpandoObject result)
    {
        var local = entry.InputScopeId is { } scopeId
            ? _state.VariableStates.FirstOrDefault(v => v.Id == scopeId)?.Variables
            : null;
        if (local is null || ((IDictionary<string, object?>)local).Count == 0)
            return result;

        var combined = new ExpandoObject();
        var combinedDict = (IDictionary<string, object?>)combined;
        foreach (var (key, value) in (IDictionary<string, object?>)local)
            combinedDict[key] = value;
        foreach (var (key, value) in (IDictionary<string, object?>)result)
            combinedDict[key] = value;
        return combined;
    }

    /// <summary>
    /// <paramref name="variables"/> without the activity's input targets: inputs are local, so a
    /// value under their name (a script returns its whole scope) reaches the enclosing scope only
    /// through an output mapping.
    /// </summary>
    private static ExpandoObject WithoutLocalInputs(Activity activity, ExpandoObject variables)
    {
        var dict = (IDictionary<string, object?>)variables;
        if (!activity.InputMappings.Any(i => dict.ContainsKey(i.Target)))
            return variables;

        var filtered = new ExpandoObject();
        var filteredDict = (IDictionary<string, object?>)filtered;
        foreach (var (key, value) in dict)
            filteredDict[key] = value;
        foreach (var input in activity.InputMappings)
            filteredDict.Remove(input.Target);
        return filtered;
    }

    private object? EvaluateMapping(string kind, string source, string target, ActivityInstanceEntry entry, ExpandoObject variables)
    {
        var evaluator = _mappingEvaluator ?? throw new InvalidOperationException(
            $"Activity '{entry.ActivityId}' has io mappings but no {nameof(IMappingExpressionEvaluator)} was supplied.");
        try
        {
            return evaluator.Evaluate(source, variables);
        }
        catch (Exception ex)
        {
            throw new MappingEvaluationException(
                $"{kind} mapping '{source}' -> '{target}' on activity '{entry.ActivityId}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>The definition element whose mappings apply to the entry: a multi-instance iteration's inner activity.</summary>
    private Activity? FindMappedActivity(ActivityInstanceEntry entry)
    {
        var activity = _definition.FindScopeForActivity(entry.ActivityId)?.FindActivity(entry.ActivityId);
        return activity is MultiInstanceActivity mi && entry.MultiInstanceIndex is not null ? mi.InnerActivity : activity;
    }

    /// <summary>Drops a completed activity's local input scope.</summary>
    private void RemoveInputScope(ActivityInstanceEntry entry)
    {
        if (entry.InputScopeId is Guid inputScopeId)
            Emit(new VariableScopesRemoved([inputScopeId]));
    }

    private sealed class MappingEvaluationException(string message, Exception inner) : Exception(message, inner);

    public void SetMultiInstanceTotal(Guid activityInstanceId, int total)
    {
        Emit(new MultiInstanceTotalSet(activityInstanceId, total));
    }

    public void RemoveVariableScopes(IReadOnlyList<Guid> scopeIds)
    {
        if (scopeIds.Count > 0)
            Emit(new VariableScopesRemoved(scopeIds.ToList()));
    }

    /// <summary>
    /// Reduces each detached scope whose branch has ended to what the branch changed (#861), or
    /// removes it when that is nothing. Nothing merges these values anywhere, so this is what keeps
    /// a branch's result visible in the instance's state without keeping a copy of all its source's
    /// variables. Call it once no activity is pending, when every completed entry's successors
    /// exist, so an unused scope has no later reader. A failed entry's scope stays whole for diagnosis.
    /// </summary>
    public void TrimEndedDetachedScopes()
    {
        if (_state.DetachedScopes.Count == 0)
            return;

        var unused = _state.DetachedScopes.Keys
            .Except(_state.GetActiveActivities().Select(e => e.VariablesId))
            .ToList();
        if (unused.Count == 0)
            return;

        var stillNeeded = new HashSet<Guid>();
        // A fork's join merges its branches into the scope the fork entry ran in.
        foreach (var fork in _state.GatewayForks)
            if (_state.FindEntry(fork.ForkInstanceId) is { } forkEntry)
                stillNeeded.Add(forkEntry.VariablesId);
        foreach (var scope in _state.VariableStates)
            if (scope.ParentVariablesId is Guid parentId)
                stillNeeded.Add(parentId);
        foreach (var watcher in _state.ConditionalWatchers)
            stillNeeded.Add(watcher.VariablesId);
        foreach (var entry in _state.Entries)
            if (entry.ErrorCode is not null)
                stillNeeded.Add(entry.VariablesId);

        var unchanged = new List<Guid>();
        foreach (var scopeId in unused.Where(id => !stillNeeded.Contains(id)))
        {
            var scope = _state.GetVariableState(scopeId);
            var source = _state.VariableStates.FirstOrDefault(v => v.Id == _state.DetachedScopes[scopeId]);
            // A source removed in the meantime leaves nothing to compare against, so all of it stays.
            var kept = (IDictionary<string, object?>)(source?.ChangesFrom(scope.Variables) ?? scope.Variables);
            if (kept.Count == 0)
                unchanged.Add(scopeId);
            else
                Emit(new VariableScopeTrimmed(scopeId,
                    ((IDictionary<string, object?>)scope.Variables).Keys.Where(name => !kept.ContainsKey(name)).ToList()));
        }
        RemoveVariableScopes(unchanged);
    }

    /// <summary>
    /// Removes finished entries and condition results nothing can read any more, so a snapshot does
    /// not keep rewriting history the journal already holds (#1050). Call it only when every
    /// completed entry's successors exist: a transition reads its completed entry. Kept are entries
    /// that are:
    /// <list type="bullet">
    /// <item>failed (error handling, transaction hazard and the stalled-instance check read them);</item>
    /// <item>completed root end events (<see cref="TryDeferredWorkflowCompletion"/>);</item>
    /// <item>inside any scope still open, at any depth (scope completion, transaction and
    /// compensation walks read a scope's children; <c>FindDescendantHazardTx</c> climbs through
    /// nested scopes that already closed). A scope whose compensation walk is still running counts
    /// as open, and so does its host: a cancelled Transaction completes before its walk ends;</item>
    /// <item>part of an open fork: the fork's entry and every entry on its tokens (a join counts
    /// their arrivals and merges their scopes);</item>
    /// <item>a complex gateway join's first entry, or a running compensation walk's thrower or
    /// handler.</item>
    /// </list>
    /// A pruned entry behaves like a completed one everywhere else: a callback for it is stale.
    /// This is not an event: pruning changes no engine decision, and replaying the journal from the
    /// start rebuilds what it removes.
    /// </summary>
    /// <returns>How many entries and condition results were removed.</returns>
    public (int Entries, int ConditionResults) PruneFinishedHistory()
    {
        var openForkTokens = _state.GatewayForks.SelectMany(f => f.CreatedTokenIds).ToHashSet();
        var referenced = _state.GatewayForks.Select(f => f.ForkInstanceId)
            .Concat(_state.ComplexGatewayJoinStates.Select(j => j.FirstActivityInstanceId))
            .Concat(_state.ActiveCompensationWalks.Keys)
            .Concat(_state.ActiveCompensationWalks.Values.Select(w => w.ThrowerActivityInstanceId))
            .Concat(_state.ActiveCompensationWalks.Values
                .Where(w => w.CurrentHandlerInstanceId.HasValue)
                .Select(w => w.CurrentHandlerInstanceId!.Value))
            .ToHashSet();

        return _state.RemoveFinishedHistory(entry =>
            entry.ErrorCode is not null
            || IsRootEndEvent(entry)
            || referenced.Contains(entry.ActivityInstanceId)
            || (entry.TokenId is { } tokenId && openForkTokens.Contains(tokenId))
            || IsInsideOpenScope(entry));
    }

    private bool IsInsideOpenScope(ActivityInstanceEntry entry)
    {
        for (var scopeId = entry.ScopeId; scopeId is { } id; scopeId = _state.FindEntry(id)?.ScopeId)
        {
            if (_state.HasActiveEntry(id) || _state.ActiveCompensationWalks.ContainsKey(id))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the entry ended a token in the root scope: an end event, or — the implicit end of
    /// BPMN §13.2 — any other completed activity with no outgoing sequence flow (#1127). Event
    /// sub-process hosts, compensation handlers and cancelled entries end no token of the main flow.
    /// </summary>
    private bool IsRootEndEvent(ActivityInstanceEntry entry)
    {
        var scope = _definition.FindScopeForActivity(entry.ActivityId);
        if (scope is not { IsRootScope: true }) return false;

        var activity = scope.GetActivity(entry.ActivityId);
        if (activity is EndEvent or EscalationEndEvent or Activities.CompensationEndEvent
            or TerminateEndEvent or SignalEndEvent)
            return true;

        return entry.IsCompleted && !entry.IsCancelled
            && activity is not (EventSubProcess or CompensationBoundaryEvent)
            && !scope.Activities.OfType<CompensationBoundaryEvent>().Any(b => b.HandlerActivityId == activity.ActivityId)
            && !scope.SequenceFlows.Any(sf => sf.Source.ActivityId == activity.ActivityId);
    }

    // ── Transaction Sub-Process outcome domain methods ───────────────────────

    /// <summary>
    /// Sets the transaction outcome to Completed (idempotent).
    /// No-op if already Cancelled (cancel takes precedence over auto-complete).
    /// </summary>
    public void SetTransactionOutcomeCompleted(Guid transactionInstanceId)
    {
        if (_state.TransactionOutcomes.TryGetValue(transactionInstanceId, out var existing))
        {
            if (existing.Outcome == TransactionOutcome.Completed) return; // idempotent
            if (existing.Outcome == TransactionOutcome.Cancelled)
                throw new InvalidOperationException(
                    $"Cannot set transaction outcome to Completed: already Cancelled (id={transactionInstanceId}).");
            if (existing.Outcome == TransactionOutcome.Hazard)
                throw new InvalidOperationException(
                    $"Cannot set transaction outcome to Completed: already Hazard (id={transactionInstanceId}).");
        }
        Emit(new TransactionOutcomeSet(transactionInstanceId, TransactionOutcome.Completed, null, null));
    }

    /// <summary>
    /// Sets the transaction outcome to Cancelled (idempotent; no-op if already Hazard).
    /// Called by Cancel End Event handling (#230).
    /// </summary>
    public void SetTransactionOutcomeCancelled(Guid transactionInstanceId)
    {
        if (_state.TransactionOutcomes.TryGetValue(transactionInstanceId, out var existing))
        {
            if (existing.Outcome == TransactionOutcome.Hazard) return;    // Hazard wins, no-op
            if (existing.Outcome == TransactionOutcome.Cancelled) return; // idempotent
            if (existing.Outcome == TransactionOutcome.Completed)
                throw new InvalidOperationException(
                    $"Cannot set transaction outcome to Cancelled: already Completed (id={transactionInstanceId}).");
        }
        Emit(new TransactionOutcomeSet(transactionInstanceId, TransactionOutcome.Cancelled, null, null));
    }

    /// <summary>
    /// Sets (or overwrites) the transaction outcome to Hazard. Hazard supersedes any prior outcome.
    /// Called by: (a) error escape from Transaction scope, and (b) compensation handler failure (#230).
    /// </summary>
    public void SetTransactionOutcomeHazard(Guid transactionInstanceId, string errorCode, string? errorMessage)
    {
        // Hazard → Hazard: re-emit to update error code/message (last writer wins).
        Emit(new TransactionOutcomeSet(transactionInstanceId, TransactionOutcome.Hazard, errorCode, errorMessage));
    }

    /// <summary>
    /// Scans for any active Transaction scope containing a completed CancelEndEvent
    /// and, for each found that has not yet been marked Cancelled, initiates the cancel flow:
    /// sets outcome to Cancelled, cancels other active scope children, and starts the compensation walk.
    /// Must be called before CompleteFinishedSubProcessScopes so the auto-complete path
    /// does not race with the cancel path and set the outcome to Completed.
    /// Returns infrastructure effects from cancelling scope children (boundary unsubscriptions, etc.).
    /// </summary>
    public List<IInfrastructureEffect> InitiateTransactionCancelFlowIfNeeded()
    {
        var effects = new List<IInfrastructureEffect>();

        foreach (var txEntry in _state.GetActiveActivities().ToList())
        {
            var scopeDef = _definition.GetScopeForActivity(txEntry.ActivityId);
            var activity = scopeDef.GetActivity(txEntry.ActivityId);
            if (activity is not Activities.Transaction) continue;

            // Idempotent: if already Cancelled or Hazard, skip
            if (_state.TransactionOutcomes.TryGetValue(txEntry.ActivityInstanceId, out var existing)
                && (existing.Outcome == TransactionOutcome.Cancelled
                    || existing.Outcome == TransactionOutcome.Hazard)) continue;

            // Look for a completed CancelEndEvent among this Transaction's scope entries
            var cancelEntry = _state.GetEntriesInScope(txEntry.ActivityInstanceId)
                .FirstOrDefault(e => e.IsCompleted && !e.IsCancelled && e.ErrorCode is null
                    && _definition.GetActivityAcrossScopes(e.ActivityId) is Activities.CancelEndEvent);

            if (cancelEntry is null) continue;

            // Phase C: if any descendant TX ended in Hazard, propagate Hazard upward and skip the walk
            var hazardDescendantId = FindDescendantHazardTx(txEntry.ActivityInstanceId);
            if (hazardDescendantId.HasValue)
            {
                SetTransactionOutcomeHazard(txEntry.ActivityInstanceId, "503",
                    $"Cancellation aborted: descendant transaction {hazardDescendantId.Value} ended in Hazard");
                // CancelScopeChildren still runs so CompleteFinishedSubProcessScopes can fire
                effects.AddRange(CancelScopeChildren(txEntry.ActivityInstanceId, "HazardCascade"));
                continue; // skip compensation walk
            }

            // 1. Mark outcome as Cancelled
            SetTransactionOutcomeCancelled(txEntry.ActivityInstanceId);

            // 2. Cancel all remaining active activities in the transaction scope
            //    (the CancelEndEvent is already completed, so it is not in GetActiveActivities)
            effects.AddRange(CancelScopeChildren(txEntry.ActivityInstanceId, "CancelledByTransaction"));

            // 3b. Abort any descendant TX compensation walks that are mid-flight (Scenario E)
            foreach (var descendantScope in DescendantScopesWithActiveWalk(txEntry.ActivityInstanceId))
                AbortCompensationWalk(descendantScope, "OuterTransactionCancellation");

            // 3. Initiate compensation walk — CancelEndEvent is the thrower, broadcast (null target)
            PerformCompensationWalk(cancelEntry.ActivityInstanceId, targetActivityRef: null);
        }

        return effects;
    }

    /// <summary>
    /// Spawns the CancelBoundaryEvent that is attached to the given Transaction host entry
    /// in the parent scope. Called when the Transaction completes with Cancelled outcome.
    /// Variables produced inside the transaction are NOT propagated — the boundary event
    /// inherits the parent scope's variables directly (BPMN spec).
    /// Returns infrastructure effects for the caller to dispatch.
    /// </summary>
    public List<IInfrastructureEffect> ActivateCancelBoundaryEvent(
        ActivityInstanceEntry txHostEntry, IWorkflowDefinition parentScopeDef)
    {
        var cancelBoundary = parentScopeDef.Activities
            .OfType<Activities.CancelBoundaryEvent>()
            .FirstOrDefault(b => b.AttachedToActivityId == txHostEntry.ActivityId);

        if (cancelBoundary is null)
            return [];

        // Spawn the CancelBoundaryEvent in the parent scope with the parent's variables.
        // Transaction variables are intentionally discarded on cancel per the BPMN spec.
        // The boundary runs in the scope the Transaction host itself runs in.
        var parentVariablesId = txHostEntry.VariablesId;

        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: cancelBoundary.ActivityId,
            ActivityType: cancelBoundary.GetType().Name,
            VariablesId: parentVariablesId,
            ScopeId: txHostEntry.ScopeId,
            MultiInstanceIndex: null,
            TokenId: null));

        return [];
    }

    /// <summary>
    /// Scans for active Transaction scopes where all scope entries are completed and at least one
    /// failed (error escaped without an inner catch), and for each not yet in a Hazard/Cancelled
    /// outcome, initiates the Hazard flow: sets outcome to Hazard and starts the compensation walk.
    /// Must be called after InitiateTransactionCancelFlowIfNeeded and before
    /// CompleteFinishedSubProcessScopes so the auto-complete path does not race.
    /// </summary>
    public List<IInfrastructureEffect> InitiateTransactionHazardFlowIfNeeded()
    {
        var effects = new List<IInfrastructureEffect>();

        foreach (var txEntry in _state.GetActiveActivities().ToList())
        {
            var scopeDef = _definition.GetScopeForActivity(txEntry.ActivityId);
            var activity = scopeDef.GetActivity(txEntry.ActivityId);
            if (activity is not Activities.Transaction) continue;

            // Idempotent: skip if already in a terminal outcome
            if (_state.TransactionOutcomes.TryGetValue(txEntry.ActivityInstanceId, out var existing)
                && existing.Outcome != TransactionOutcome.Completed) continue;

            var scopeEntries = _state.GetEntriesInScope(txEntry.ActivityInstanceId).ToList();
            if (!scopeEntries.All(e => e.IsCompleted)) continue;

            var failedEntry = scopeEntries.FirstOrDefault(e => e.ErrorCode is not null);
            if (failedEntry is null) continue;

            SetTransactionOutcomeHazard(txEntry.ActivityInstanceId, failedEntry.ErrorCode!, failedEntry.ErrorState?.Message);
            effects.AddRange(CancelScopeChildren(txEntry.ActivityInstanceId, "CancelledByHazard"));
            PerformCompensationWalk(failedEntry.ActivityInstanceId, targetActivityRef: null);
        }

        return effects;
    }

    /// <summary>
    /// Activates the ErrorBoundaryEvent on the Transaction host after the Hazard compensation walk
    /// completes. Spawns the boundary in the parent scope (if defined), or fails the TX host so
    /// the error propagates normally. Analog of ActivateCancelBoundaryEvent for the Hazard path.
    /// </summary>
    public List<IInfrastructureEffect> ActivateHazardErrorBoundaryEvent(
        ActivityInstanceEntry txHostEntry,
        IWorkflowDefinition parentScopeDef,
        string errorCode,
        string? errorMessage)
    {
        var effects = new List<IInfrastructureEffect>();

        var errorBoundary = parentScopeDef.Activities
            .OfType<Activities.BoundaryErrorEvent>()
            .Where(b => b.AttachedToActivityId == txHostEntry.ActivityId
                && (b.ErrorCode == null || b.ErrorCode == errorCode))
            .OrderByDescending(b => b.ErrorCode != null)
            .FirstOrDefault();

        if (errorBoundary is null)
        {
            // No Error Boundary on TX host — fail it so the error propagates to parent scope
            Emit(new ActivityFailed(txHostEntry.ActivityInstanceId, errorCode, errorMessage ?? string.Empty));
            effects.AddRange(BuildBoundaryUnsubscribeEffects(txHostEntry.ActivityId, txHostEntry));
            return effects;
        }

        Emit(new ActivityCancelled(
            txHostEntry.ActivityInstanceId,
            $"Interrupted by error boundary '{errorBoundary.ActivityId}'"));
        effects.AddRange(BuildBoundaryUnsubscribeEffects(txHostEntry.ActivityId, txHostEntry));

        // The boundary runs in the scope the Transaction host itself runs in.
        var parentVariablesId = txHostEntry.VariablesId;

        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: errorBoundary.ActivityId,
            ActivityType: errorBoundary.GetType().Name,
            VariablesId: parentVariablesId,
            ScopeId: txHostEntry.ScopeId,
            MultiInstanceIndex: null,
            TokenId: null));

        return effects;
    }

    /// <summary>
    /// Scans active Transaction entries in Hazard state whose compensation walk is complete and
    /// all scope entries are done, and activates the Error Boundary Event (or fails the TX host).
    /// Called on every HandleScopeCompletions iteration so comp walk completion is not missed.
    /// </summary>
    public List<IInfrastructureEffect> ActivatePendingHazardBoundaries()
    {
        var effects = new List<IInfrastructureEffect>();

        foreach (var txEntry in _state.GetActiveActivities().ToList())
        {
            if (!_state.TransactionOutcomes.TryGetValue(txEntry.ActivityInstanceId, out var outcome)
                || outcome.Outcome != TransactionOutcome.Hazard) continue;

            if (_state.ActiveCompensationWalks.ContainsKey(txEntry.ActivityInstanceId)) continue;

            var scopeEntries = _state.GetEntriesInScope(txEntry.ActivityInstanceId).ToList();
            if (!scopeEntries.All(e => e.IsCompleted)) continue;

            var failedChild = scopeEntries.First(e => e.ErrorCode is not null);
            var parentScopeDef = _definition.GetScopeForActivity(txEntry.ActivityId);

            effects.AddRange(ActivateHazardErrorBoundaryEvent(
                txEntry, parentScopeDef, failedChild.ErrorCode!, failedChild.ErrorState?.Message));
        }

        return effects;
    }

    public void ResolveTransitions(IReadOnlyList<CompletedActivityTransitions> completedTransitions)
    {
        foreach (var completed in completedTransitions)
        {
            var completedEntry = _state.GetEntry(completed.ActivityInstanceId);

            // Failed or cancelled entries don't transition, and nothing spawns into a scope that
            // a terminate end event ended.
            if (completedEntry.ErrorCode is not null || completedEntry.IsCancelled
                || _terminatedScopes.Contains(completedEntry.ScopeId))
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
                    if (otherActive || _transitionsPending)
                        break; // Defer — TryDeferredWorkflowCompletion completes it at the end of the loop

                    // Unregister any still-armed root-scope event sub-process listeners.
                    effects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
                        _definition, scopeContainerId: null,
                        scopeVariablesId: _state.GetRootVariablesId(),
                        skipStartEventActivityId: null));

                    Emit(new WorkflowCompleted());
                    // If this is a child workflow, notify parent of completion.
                    // When an escalation was also raised in this batch, the parent may
                    // have already cancelled the host entry (interrupting boundary) —
                    // the parent's stale guard in OnChildWorkflowCompleted safely
                    // ignores the completion in that case.
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
                    ProcessOpenSubProcess(openSub, activityInstanceId, effects);
                    break;

                case RegisterTimerCommand timer:
                    effects.Add(new RegisterTimerEffect(
                        _state.Id, activityInstanceId,
                        timer.TimerActivityId, timer.DueTime));
                    break;

                case RegisterMessageCommand msg:
                    // Build-time exception (e.g. correlation variable missing in
                    // ProcessRegisterMessage) routes to FailActivity inline so the workflow
                    // surfaces the failure in state, not just logs (#425). Subsequent commands
                    // targeting the same activity are naturally no-op'd by the aggregate's
                    // "Each activity instance executes at most once" invariant
                    // (FailActivity's IsCompleted stale-guard).
                    try
                    {
                        effects.Add(ProcessRegisterMessage(msg, activityInstanceId));
                    }
                    catch (Exception ex)
                    {
                        var failEffects = FailActivity(msg.ActivityId, activityInstanceId, ex);
                        effects.AddRange(failEffects);
                    }
                    break;

                case RegisterSignalCommand signal:
                    effects.Add(new SubscribeSignalEffect(
                        signal.SignalName, _state.Id, signal.ActivityId, activityInstanceId));
                    break;

                case StartChildWorkflowCommand startChild:
                    // Note: ProcessStartChildWorkflow emits ChildWorkflowLinked before
                    // BuildChildInputVariables runs; a throw leaves a phantom link entry.
                    // This is acceptable — the child grain never receives StartWorkflow.
                    try
                    {
                        effects.Add(ProcessStartChildWorkflow(startChild, activityInstanceId));
                    }
                    catch (Exception ex)
                    {
                        var activityId = _state.FindEntry(activityInstanceId)?.ActivityId;
                        if (activityId is not null)
                            effects.AddRange(FailActivity(activityId, activityInstanceId, ex));
                    }
                    break;

                case AddConditionsCommand conditions:
                    try
                    {
                        effects.AddRange(ProcessAddConditions(conditions, activityInstanceId));
                    }
                    catch (Exception ex)
                    {
                        var activityId = _state.FindEntry(activityInstanceId)?.ActivityId;
                        if (activityId is not null)
                            effects.AddRange(FailActivity(activityId, activityInstanceId, ex));
                    }
                    break;

                case EvaluateActivationConditionCommand evalActivation:
                    try
                    {
                        effects.Add(ProcessEvaluateActivationCondition(evalActivation, activityInstanceId));
                    }
                    catch (Exception ex)
                    {
                        var activityId = _state.FindEntry(activityInstanceId)?.ActivityId;
                        if (activityId is not null)
                            effects.AddRange(FailActivity(activityId, activityInstanceId, ex));
                    }
                    break;

                case RegisterUserTaskCommand regUserTask:
                    Emit(new UserTaskRegistered(
                        activityInstanceId, regUserTask.Assignee,
                        regUserTask.CandidateGroups.ToList(), regUserTask.CandidateUsers.ToList(),
                        regUserTask.ExpectedOutputVariables?.ToList()));
                    var currentEntry = _state.GetActiveEntry(activityInstanceId);
                    effects.Add(new RegisterUserTaskEffect(
                        _state.Id, activityInstanceId,
                        currentEntry.ActivityId, regUserTask.Assignee,
                        regUserTask.CandidateGroups.ToList(), regUserTask.CandidateUsers.ToList(),
                        regUserTask.ExpectedOutputVariables?.ToList()));
                    break;

                case ThrowSignalCommand throwSignal:
                    effects.Add(new ThrowSignalEffect(throwSignal.SignalName));
                    break;

                case ThrowEscalationCommand throwEscalation:
                    effects.AddRange(ProcessThrowEscalation(throwEscalation, activityInstanceId));
                    break;

                case ThrowErrorCommand throwError:
                    // An error end event fails itself, so the error takes the failed-task path:
                    // boundary, error event sub-process, or the parent of a call activity (#843).
                    if (_state.FindEntry(activityInstanceId) is { IsCompleted: false } thrower)
                    {
                        var code = throwError.ErrorCode ?? UnnamedErrorCode;
                        effects.AddRange(FailActivity(thrower.ActivityId, activityInstanceId,
                            new BpmnErrorActivityException(code,
                                $"Error end event '{thrower.ActivityId}' threw error '{code}'")));
                    }
                    break;

                case TerminateScopeCommand:
                    effects.AddRange(TerminateScope(activityInstanceId));
                    break;

                case DiscardLateTokenCommand discard:
                    Emit(new ActivityCancelled(activityInstanceId, discard.Reason));
                    if (_state.FindEntry(activityInstanceId) is { } lateEntry
                        && _state.GetComplexGatewayJoinState(lateEntry.ActivityId, lateEntry.ScopeId) is { HasFired: true })
                    {
                        Emit(new ComplexGatewayJoinStateTokenIncremented(lateEntry.ActivityId, lateEntry.ScopeId));
                        RemoveComplexGatewayJoinIfDone(lateEntry.ActivityId, lateEntry.ScopeId);
                    }
                    break;

                case RegisterConditionalWatcherCommand conditionalWatcher:
                    Emit(new ConditionalWatcherRegistered(
                        activityInstanceId, conditionalWatcher.ActivityId,
                        conditionalWatcher.ConditionExpression, conditionalWatcher.VariablesId));
                    break;

                case CompensationRequestedCommand compensation:
                    PerformCompensationWalk(compensation.ThrowerActivityInstanceId, compensation.TargetActivityRef);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown execution command type: {command.GetType().Name}");
            }
        }

        return effects.AsReadOnly();
    }

    /// <summary>
    /// Checks for deferred workflow completion. When a CompleteWorkflowCommand was deferred
    /// (because other activities were still active), and those activities have since been
    /// cancelled or completed, this method emits WorkflowCompleted.
    /// Returns infrastructure effects (e.g., parent notification) that need to be performed.
    /// <para>
    /// This is intentionally general-purpose — it applies to ANY workflow where an EndEvent
    /// completes before all activities finish (e.g., Complex Gateway early-activation discards
    /// remaining branches, parallel paths with different lengths, or cancelled activities).
    /// It is not specific to Complex Gateway.
    /// </para>
    /// </summary>
    public IReadOnlyList<IInfrastructureEffect> TryDeferredWorkflowCompletion()
    {
        if (_state.IsCompleted)
            return [];

        if (_state.GetActiveActivities().Any())
            return [];

        // Check if a root-scope end event (EndEvent or CompensationEndEvent) was completed — not
        // failed: an end event whose input mapping fails to evaluate does not complete the instance.
        var hasCompletedEndEvent = _state.GetCompletedActivities()
            .Any(e => e.ErrorCode is null && IsRootEndEvent(e));

        if (!hasCompletedEndEvent)
            return [];

        var effects = new List<IInfrastructureEffect>();

        effects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
            _definition, scopeContainerId: null,
            scopeVariablesId: _state.GetRootVariablesId(),
            skipStartEventActivityId: null));

        Emit(new WorkflowCompleted());

        if (_state.ParentWorkflowInstanceId.HasValue)
        {
            var rootVariables = _state.GetMergedVariables(_state.GetRootVariablesId());
            effects.Add(new NotifyParentCompletedEffect(
                _state.ParentWorkflowInstanceId.Value,
                _state.ParentActivityId!,
                rootVariables));
        }

        return effects.AsReadOnly();
    }

    /// <summary>
    /// Fails the workflow instance when an unhandled activity failure has left it with no
    /// live tokens (#762). Called at quiescence (end of the execution loop), after
    /// <see cref="TryDeferredWorkflowCompletion"/> — so a workflow whose root end event already
    /// completed still completes normally.
    /// <para>
    /// Without this, an unhandled failure (no error boundary, no error event sub-process) on
    /// the last live branch leaves the instance with no active activities while it is neither
    /// completed nor failed: the token is silently dropped and nothing ever terminates the
    /// instance. A failed activity entry anywhere in the instance is the signal that the stall
    /// was caused by an error; an instance with no active activities and no failure is left
    /// alone (that is not an error path this method owns).
    /// </para>
    /// <para>
    /// Child workflows are marked failed too; the parent notification for the unhandled
    /// failure is still emitted by <see cref="FailActivity"/>, so it is not repeated here.
    /// </para>
    /// </summary>
    public IReadOnlyList<IInfrastructureEffect> TryFailStalledWorkflow()
    {
        if (!_state.IsStarted || _state.IsCompleted)
            return [];

        if (_state.GetActiveActivities().Any())
            return [];

        var failedEntry = _state.Entries
            .Where(e => e.ErrorCode is not null)
            .OrderBy(e => e.CompletedAt ?? DateTimeOffset.MinValue)
            .LastOrDefault();
        if (failedEntry is null)
            return [];

        var effects = new List<IInfrastructureEffect>();

        // Unregister any still-armed root-scope event sub-process listeners — the instance is
        // terminal, so a later timer/message/signal must not re-open it.
        effects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
            _definition, scopeContainerId: null,
            scopeVariablesId: _state.GetRootVariablesId(),
            skipStartEventActivityId: null));

        Emit(new WorkflowFailed(
            failedEntry.ActivityId,
            failedEntry.ActivityInstanceId,
            failedEntry.ErrorCode!,
            failedEntry.ErrorState?.Message ?? string.Empty));

        return effects.AsReadOnly();
    }

    // ── Compensation Walk ────────────────────────────────────────────────────────────

    /// <summary>
    /// Initiates a compensation walk for the scope that contains the thrower activity.
    /// Resolves the scope from the thrower's ScopeId, filters and sorts the compensation
    /// log, emits CompensationWalkStarted, then spawns the first handler.
    /// Called from ProcessCommands when a CompensationRequestedCommand is encountered.
    /// </summary>
    private void PerformCompensationWalk(Guid throwerActivityInstanceId, string? targetActivityRef)
    {
        var throwerEntry = _state.GetEntry(throwerActivityInstanceId);
        var scopeId = throwerEntry.ScopeId; // null = root scope

        var scopeDef = GetScopeDefinition(scopeId);

        var snapshots = _state.CompensationLog
            .Where(s => s.ScopeId == scopeId && !s.IsCompensated)
            .Where(s => targetActivityRef is null || s.ActivityDefinitionId == targetActivityRef)
            .OrderByDescending(s => s.CompletedAtSequence)
            .ToList();

        if (snapshots.Count == 0)
        {
            Emit(new CompensationWalkStarted(scopeId, targetActivityRef, 0, throwerActivityInstanceId));
            Emit(new CompensationWalkCompleted(scopeId));
            // Complete the thrower so it transitions normally — unless already completed
            // (e.g. CancelEndEvent is pre-completed before the cancel walk is initiated).
            if (!throwerEntry.IsCompleted)
                Emit(new ActivityCompleted(throwerActivityInstanceId, throwerEntry.VariablesId, new System.Dynamic.ExpandoObject()));
            return;
        }

        Emit(new CompensationWalkStarted(scopeId, targetActivityRef, snapshots.Count, throwerActivityInstanceId));
        AdvanceCompensationWalk(scopeId, scopeDef, snapshots);
    }

    /// <summary>
    /// Spawns the next compensation handler from the active walk.
    /// Called both from PerformCompensationWalk (for the first handler) and from
    /// AdvanceCompensationWalkIfHandlerCompleted (for subsequent handlers).
    /// </summary>
    private void AdvanceCompensationWalk(Guid? scopeId, IWorkflowDefinition scopeDef,
        List<CompletedActivitySnapshot> orderedSnapshots)
    {
        var walk = _state.GetCompensationWalk(scopeId);
        if (walk is null) return;

        // Find the next uncompensated snapshot that has a compensation handler
        CompletedActivitySnapshot? snapshot = null;
        Activities.CompensationBoundaryEvent? boundary = null;
        foreach (var candidate in orderedSnapshots.Where(s => !s.IsCompensated))
        {
            boundary = scopeDef.FindCompensationBoundary(candidate.ActivityDefinitionId);
            if (boundary is not null)
            {
                snapshot = candidate;
                break;
            }
        }

        if (snapshot is null)
        {
            var throwerInstanceId = walk.ThrowerActivityInstanceId;
            var throwerEntry = _state.GetEntry(throwerInstanceId);
            Emit(new CompensationWalkCompleted(scopeId));
            // Complete the thrower so it can transition to its outgoing flow
            if (!throwerEntry.IsCompleted)
                Emit(new ActivityCompleted(throwerInstanceId, throwerEntry.VariablesId, new System.Dynamic.ExpandoObject()));
            return;
        }

        // Find the handler activity definition in the scope
        var handlerActivity = scopeDef.GetActivity(boundary.HandlerActivityId);

        // parent → snapshot scope → handler scope. The handler reads the parent's current
        // variables, so it sees side effects that earlier handlers in this walk merged back
        // (#326); the snapshot scope overlays them, so on a shared key the handler sees the value
        // at the compensated activity's completion, not a later overwrite or another loop
        // iteration's value (#841). The handler's own writes stay in its own scope, which is all
        // that merges back — the snapshot's stale values never overwrite the parent.
        var parentVariablesId = CompensationWalkVariablesId(scopeId);
        var snapshotVariablesId = Guid.NewGuid();
        Emit(new ChildVariableScopeCreated(snapshotVariablesId, parentVariablesId));
        Emit(new VariablesMerged(snapshotVariablesId, snapshot.VariablesSnapshot));
        var handlerVariablesId = Guid.NewGuid();
        Emit(new ChildVariableScopeCreated(handlerVariablesId, snapshotVariablesId));

        // Spawn the handler activity
        var handlerInstanceId = Guid.NewGuid();
        Emit(new ActivitySpawned(
            ActivityInstanceId: handlerInstanceId,
            ActivityId: handlerActivity.ActivityId,
            ActivityType: handlerActivity.GetType().Name,
            VariablesId: handlerVariablesId,
            ScopeId: scopeId,
            MultiInstanceIndex: null,
            TokenId: null));

        Emit(new CompensationHandlerSpawned(
            handlerInstanceId, snapshot.ActivityDefinitionId, handlerActivity.ActivityId, scopeId,
            snapshot.ActivityInstanceId));
        // Apply(CompensationHandlerSpawned) sets CurrentHandlerInstanceId and marks entry as handler
    }

    /// <summary>
    /// Called from HandleScopeCompletions (grain infrastructure) after CompleteFinishedSubProcessScopes
    /// to check if the active compensation handler has finished, and if so advance the walk.
    /// Returns the thrower's instance ID if the walk completed (so the caller can compute transitions),
    /// or null if the walk is still in progress or was not active.
    /// </summary>
    public Guid? AdvanceCompensationWalkIfHandlerCompleted()
    {
        var walk = _state.GetActiveWalkWithHandler();
        if (walk is null) return null;

        var handlerEntry = _state.FindEntry(walk.CurrentHandlerInstanceId.Value);
        if (handlerEntry is null || !handlerEntry.IsCompleted) return null;

        if (handlerEntry.ErrorCode is not null)
        {
            Emit(new CompensationWalkFailed(
                walk.ScopeId,
                walk.CurrentHandlerInstanceId.Value,
                handlerEntry.ErrorCode,
                handlerEntry.ErrorMessage ?? "Compensation handler failed"));
            Emit(new WorkflowCompleted());
            return null;
        }

        // Handler completed successfully; find the compensable activity def ID via the boundary.
        var scopeId = walk.ScopeId;
        var scopeDef = GetScopeDefinition(scopeId);
        var handlerActivityId = handlerEntry.ActivityId;
        var boundary = scopeDef.Activities.OfType<Activities.CompensationBoundaryEvent>()
            .FirstOrDefault(b => b.HandlerActivityId == handlerActivityId);

        if (boundary is not null && walk.CurrentCompensatedActivityInstanceId is Guid compensatedId)
        {
            Emit(new CompensationEntryMarkedCompensated(boundary.AttachedToActivityId, scopeId, compensatedId));
        }

        // Propagate the handler's own writes up to the enclosing scope so compensation side
        // effects are visible after the walk, and to later handlers before the next one spawns.
        // Only keys the handler changed: a script handler returns its whole context, so a key it
        // merely read (from the snapshot or the parent) must not overwrite the parent.
        var parentVariablesId = CompensationWalkVariablesId(scopeId);
        var handlerScope = _state.GetVariableState(handlerEntry.VariablesId);
        var seen = handlerScope.ParentVariablesId is Guid seenId
            ? (IDictionary<string, object?>)_state.GetMergedVariables(seenId)
            : new Dictionary<string, object?>();
        var changed = new ExpandoObject();
        var changedDict = (IDictionary<string, object?>)changed;
        foreach (var (key, value) in (IDictionary<string, object?>)handlerScope.Variables)
            if (!seen.TryGetValue(key, out var before) || !Equals(before, value))
                changedDict[key] = value;
        if (changedDict.Count > 0)
            Emit(new VariablesMerged(parentVariablesId, changed));

        // Build the remaining ordered snapshot list from current log state
        var remainingSnapshots = _state.CompensationLog
            .Where(s => s.ScopeId == scopeId && !s.IsCompensated)
            .Where(s => walk.TargetActivityRef is null || s.ActivityDefinitionId == walk.TargetActivityRef)
            .OrderByDescending(s => s.CompletedAtSequence)
            .ToList();

        AdvanceCompensationWalk(scopeId, scopeDef, remainingSnapshots);

        // If the walk completed (no more handlers), the thrower was completed inside AdvanceCompensationWalk.
        // Return its instance ID so the caller can compute transitions for it.
        if (_state.GetCompensationWalk(scopeId) is null)
            return walk.ThrowerActivityInstanceId;

        return null;
    }

    /// <summary>
    /// The variable scope a compensation walk runs in: the root scope, or the execution scope a
    /// sub-process host opened for its children (the host entry itself carries its parent's).
    /// </summary>
    private Guid CompensationWalkVariablesId(Guid? scopeId)
    {
        if (scopeId is not Guid hostId) return _state.GetRootVariablesId();
        if (_state.ScopeHostExecutionScopes.TryGetValue(hostId, out var ownScopeId)
            && _state.VariableStates.Any(s => s.Id == ownScopeId))
            return ownScopeId;
        return _state.GetEntriesInScope(hostId)
                   .Where(e => !e.IsCompensationHandler)
                   .Select(e => (Guid?)e.VariablesId)
                   .FirstOrDefault()
               ?? _state.GetEntry(hostId).VariablesId;
    }

    /// <summary>
    /// Records the completion-time snapshot of an activity with a compensation boundary. A
    /// multi-instance iteration is compensated on its own (BPMN §10.6), so its snapshot belongs
    /// to the scope that holds the multi-instance host, where a throw finds it (#841).
    /// </summary>
    private void RecordCompensationSnapshotIfCompensable(ActivityInstanceEntry entry, ExpandoObject variables)
    {
        if (entry.IsCompensationHandler) return;
        var scopeDef = _definition.FindScopeForActivity(entry.ActivityId);
        if (scopeDef?.FindCompensationBoundary(entry.ActivityId) is null) return;
        // A multi-instance host's iterations carry the snapshots, not the host.
        if (entry.MultiInstanceIndex is null && entry.ActivityType == nameof(MultiInstanceActivity)) return;
        var scopeId = entry.MultiInstanceIndex is not null && entry.ScopeId is Guid hostId
            ? _state.GetEntry(hostId).ScopeId
            : entry.ScopeId;
        Emit(new CompensableActivitySnapshotRecorded(
            entry.ActivityInstanceId, entry.ActivityId, DeepCopyExpandoObject(variables), scopeId));
    }

    /// <summary>Gets the IWorkflowDefinition for a scope instance (null = root scope).</summary>
    private IWorkflowDefinition GetScopeDefinition(Guid? scopeId)
    {
        if (scopeId is null) return _definition;

        var scopeEntry = _state.GetEntry(scopeId.Value);
        var parentDef = _definition.GetScopeForActivity(scopeEntry.ActivityId);
        return (IWorkflowDefinition)parentDef.GetActivity(scopeEntry.ActivityId);
    }

    private static ExpandoObject DeepCopyExpandoObject(ExpandoObject source)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(source);
        return Newtonsoft.Json.JsonConvert.DeserializeObject<ExpandoObject>(
            json, new Newtonsoft.Json.Converters.ExpandoObjectConverter())!;
    }

    /// <summary>The error code of an error end event that names no error: only catch-all handlers match it.</summary>
    internal const string UnnamedErrorCode = "error";

    /// <summary>
    /// A terminate end event ends its scope (#843): every other active entry of the scope is
    /// cancelled with its nested scopes and listeners. The root scope then completes through the
    /// event's own <see cref="CompleteWorkflowCommand"/>; a sub-process completes once its scope
    /// is empty, as after a plain end event.
    /// </summary>
    // Scopes a terminate end event ended (null = the process): a sibling that completed earlier in
    // the same pass must not spawn its successor after the terminate. Not state.
    private readonly HashSet<Guid?> _terminatedScopes = [];

    private List<IInfrastructureEffect> TerminateScope(Guid terminatorInstanceId)
    {
        var effects = new List<IInfrastructureEffect>();
        if (_state.FindEntry(terminatorInstanceId) is not { } terminator)
            return effects;

        _terminatedScopes.Add(terminator.ScopeId);
        var reason = $"Terminated by end event '{terminator.ActivityId}'";
        foreach (var sibling in _state.GetActiveActivities()
            .Where(e => e.ScopeId == terminator.ScopeId && e.ActivityInstanceId != terminatorInstanceId)
            .ToList())
        {
            if (_state.HasActiveChildrenInScope(sibling.ActivityInstanceId))
                effects.AddRange(CancelScopeChildren(sibling.ActivityInstanceId, reason));
            Emit(new ActivityCancelled(sibling.ActivityInstanceId, reason));
            effects.AddRange(BuildCancelledEntryCleanupEffects(sibling));
        }
        return effects;
    }

    private IReadOnlyList<IInfrastructureEffect> ProcessThrowEscalation(
        ThrowEscalationCommand command, Guid activityInstanceId)
    {
        var entry = _state.FindEntry(activityInstanceId);
        if (entry is null)
            return [];

        // Use the definition's scope walker to find a matching boundary handler
        var boundaryHandler = _definition.FindBoundaryEscalationHandler(
            entry.ActivityId, command.EscalationCode);

        var espEffects = new List<IInfrastructureEffect>();
        if (TryActivateEscalationEventSubProcess(entry, command.EscalationCode,
                _state.GetMergedVariables(entry.VariablesId), boundaryHandler?.Scope, espEffects) is not null)
            return espEffects;

        if (boundaryHandler is not null)
        {
            var (boundaryEvent, _, attachedToActivityId) = boundaryHandler.Value;
            var attachedEntry = FindBoundaryHost(entry, attachedToActivityId);
            if (attachedEntry is null)
                return [];

            var scopeVariables = _state.GetMergedVariables(entry.VariablesId);
            return HandleEscalationBoundaryMatch(boundaryEvent, attachedEntry, scopeVariables).Effects;
        }

        // No boundary found — escalate to parent grain if this is a child workflow
        if (_state.ParentWorkflowInstanceId.HasValue)
        {
            var scopeVariables = _state.GetMergedVariables(entry.VariablesId);
            return [new NotifyParentEscalationRaisedEffect(
                _state.ParentWorkflowInstanceId.Value,
                _state.Id,
                _state.ParentActivityId!,
                command.EscalationCode,
                scopeVariables,
                EscalationInstanceId: activityInstanceId)];
        }

        // Uncaught at root — BPMN spec: escalation is non-faulting, just record it
        Emit(new EscalationUncaughtRaised(command.EscalationCode, entry.ActivityId));
        return [];
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

    private void ProcessOpenSubProcess(
        OpenSubProcessCommand openSub, Guid hostActivityInstanceId,
        List<IInfrastructureEffect> effects)
    {
        var newScopeId = Guid.NewGuid();
        Emit(new ChildVariableScopeCreated(newScopeId, openSub.ParentVariablesId));
        // Bind the host activity instance to its own execution scope so that
        // CompleteFinishedSubProcessScopes can disambiguate the host scope
        // from sibling EventSubProcess handler scopes that share the same
        // ParentVariablesId. See issue #284.
        Emit(new ScopeHostExecutionScopeOpened(hostActivityInstanceId, newScopeId));

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

        // Register any event sub-process timers / message listeners declared directly
        // in this SubProcess scope — they are keyed to the SubProcess host instance so
        // they are uniquely cleaned up on scope exit.
        effects.AddRange(BuildEventSubProcessTimerRegistrations(openSub.SubProcess, hostActivityInstanceId));
        effects.AddRange(BuildEventSubProcessMessageRegistrations(
            openSub.SubProcess, hostActivityInstanceId, newScopeId));
        effects.AddRange(BuildEventSubProcessSignalRegistrations(
            openSub.SubProcess, hostActivityInstanceId));
    }

    /// <summary>
    /// Enumerates event sub-process timers declared directly in <paramref name="scope"/>
    /// and produces a <see cref="RegisterTimerEffect"/> for each one. The timer grain key
    /// uses <paramref name="scopeContainerId"/> as the host id so registrations are uniquely
    /// scoped to a specific scope instance (a re-entered SubProcess gets a fresh host id).
    /// </summary>
    private List<IInfrastructureEffect> BuildEventSubProcessTimerRegistrations(
        IWorkflowDefinition scope, Guid scopeContainerId)
    {
        var list = new List<IInfrastructureEffect>();
        foreach (var (_, timerStart) in scope.GetEventSubProcessTimers())
        {
            var dueTime = timerStart.TimerDefinition.GetDueTime();
            list.Add(new RegisterTimerEffect(
                _state.Id, scopeContainerId, timerStart.ActivityId, dueTime));
        }
        return list;
    }

    /// <summary>
    /// Enumerates event sub-process signal start events declared directly in
    /// <paramref name="scope"/> and produces a <see cref="SubscribeSignalEffect"/> for each.
    /// </summary>
    private List<IInfrastructureEffect> BuildEventSubProcessSignalRegistrations(
        IWorkflowDefinition scope, Guid scopeContainerId)
    {
        var list = new List<IInfrastructureEffect>();
        foreach (var (_, signalStart) in scope.GetEventSubProcessSignals())
        {
            var signalDef = _definition.GetSignalDefinition(signalStart.SignalDefinitionId);
            list.Add(new SubscribeSignalEffect(
                signalDef.Name, _state.Id, signalStart.ActivityId, scopeContainerId));
        }
        return list;
    }

    /// <summary>
    /// Enumerates event sub-process message start events declared directly in
    /// <paramref name="scope"/> and produces a <see cref="SubscribeMessageEffect"/> for each.
    /// Correlation key is resolved against the enclosing scope's variables.
    /// </summary>
    private List<IInfrastructureEffect> BuildEventSubProcessMessageRegistrations(
        IWorkflowDefinition scope, Guid scopeContainerId, Guid scopeVariablesId)
    {
        var list = new List<IInfrastructureEffect>();
        foreach (var (_, messageStart) in scope.GetEventSubProcessMessages())
        {
            var messageDef = _definition.GetMessageDefinition(messageStart.MessageDefinitionId);
            var correlationKey = ResolveCorrelationKey(messageDef, scopeVariablesId);
            list.Add(new SubscribeMessageEffect(
                messageDef.Name, correlationKey,
                _state.Id, messageStart.ActivityId, scopeContainerId));
        }
        return list;
    }

    /// <summary>
    /// Root-scope variant of <see cref="BuildEventSubProcessTimerRegistrations(IWorkflowDefinition, Guid)"/>.
    /// At the root scope there is no SubProcess host activity instance, so the workflow
    /// instance id (<c>_state.Id</c>) is used as a stable synthetic host id. Invoked
    /// internally from <see cref="MarkExecutionStarted"/> — the aggregate is the single
    /// owner of scope-entry effect assembly, so this is intentionally not public.
    /// </summary>
    private IReadOnlyList<IInfrastructureEffect> BuildRootScopeEntryEffects()
    {
        var list = new List<IInfrastructureEffect>();
        list.AddRange(BuildEventSubProcessTimerRegistrations(_definition, _state.Id));
        list.AddRange(BuildEventSubProcessMessageRegistrations(
            _definition, _state.Id, _state.GetRootVariablesId()));
        list.AddRange(BuildEventSubProcessSignalRegistrations(_definition, _state.Id));
        return list.AsReadOnly();
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

        // Catch/boundary message subscriptions are partitioned by correlation key
        // (MessageCorrelationGrain is keyed "{name}/{key}"); an uncorrelated message can only
        // trigger a message start event. Reject here so the registration-path failure routes
        // to FailActivity instead of escaping the effect handler and stalling the workflow (#761).
        if (string.IsNullOrWhiteSpace(correlationKey))
            throw new InvalidOperationException(
                $"Message '{messageDef.Name}' has no correlation key. Intermediate catch and boundary " +
                "message events require a correlation key (zeebe:subscription correlationKey).");

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

        var parentVariables = _state.GetMergedVariables(entry.ExecutionVariablesId);
        var inputVariables = callActivity.BuildChildInputVariables(parentVariables);

        return new StartChildWorkflowEffect(
            childInstanceId, callActivity.CalledProcessKey,
            inputVariables, callActivity.ActivityId,
            activityInstanceId);
    }

    private IInfrastructureEffect ProcessEvaluateActivationCondition(
        EvaluateActivationConditionCommand cmd, Guid activityInstanceId)
    {
        var entry = _state.GetActiveEntry(activityInstanceId);
        var tokenCount = (_state.GetComplexGatewayJoinState(entry.ActivityId, entry.ScopeId)?.WaitingTokenCount ?? 0) + 1;
        var effect = new PublishDomainEventEffect(new EvaluateActivationConditionEvent(
            _state.Id,
            _definition.WorkflowId,
            _definition.ProcessDefinitionId,
            activityInstanceId,
            entry.ActivityId,
            cmd.Condition,
            entry.VariablesId,
            tokenCount));

        // Count the token last: if anything above throws, the caller fails the activity and the
        // failed token must not be on the join state.
        CreateOrIncrementComplexGatewayJoinToken(entry, cmd.Condition);
        return effect;
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

    /// <summary>
    /// Returns true if a timer fire for the given (timerActivityId, hostActivityInstanceId)
    /// would be caught by HandleTimerFired's internal stale guards. Used by the grain to
    /// short-circuit late fires before aggregate dispatch — both for cycle savings and to
    /// emit an observability warning when TimerCallbackGrain.Cancel swallowed a transient
    /// UnregisterReminder exception (per the registration-vs-cleanup asymmetry rule).
    ///
    /// Mirrors the guards in HandleTimerFired and TryActivateTimerEventSubProcess. Kept as
    /// a duplicate predicate (~25 lines) rather than extracted; a follow-up can refactor
    /// into a shared private helper if a third caller appears.
    /// </summary>
    public bool IsTimerFireStale(string timerActivityId, Guid hostActivityInstanceId)
    {
        // A reminder armed for a timer a migration renamed fires under the old id; one a
        // migration removed has no element any more (#1069).
        timerActivityId = CurrentActivityId(timerActivityId);
        if (IsUnknownActivity(timerActivityId))
            return true;

        var espMatch = _definition.FindEventSubProcessByStartEvent(timerActivityId);
        if (espMatch is not null && espMatch.Value.EventSubProcess.Activities
                .OfType<TimerStartEvent>()
                .Any(t => t.ActivityId == timerActivityId))
        {
            // ESP timer: root-scope guard is _state.IsCompleted; SubProcess-scoped guard
            // is the container entry being missing or completed (mirrors
            // TryActivateTimerEventSubProcess).
            if (hostActivityInstanceId == _state.Id)
                return _state.IsCompleted;
            var containerEntry = _state.FindEntry(hostActivityInstanceId);
            return containerEntry is null || containerEntry.IsCompleted;
        }

        // Regular timer (intermediate catch, boundary, multiple-event boundary,
        // multiple-intermediate-catch): host entry must be present and not completed
        // (mirrors HandleTimerFired's :1262-1265 stale guard).
        var entry = _state.FindEntry(hostActivityInstanceId);
        return entry is null || entry.IsCompleted || IsIterationAsBoundaryHost(timerActivityId, entry);
    }

    /// <summary>
    /// A boundary belongs to the multi-instance host; an iteration shares the host's activity id but
    /// never hosts the boundary, so a boundary trigger keyed by an iteration is stale (#842).
    /// </summary>
    private bool IsIterationAsBoundaryHost(string boundaryActivityId, ActivityInstanceEntry hostEntry)
        => hostEntry.MultiInstanceIndex is not null
            && _definition.GetActivityAcrossScopes(boundaryActivityId)
                is BoundaryTimerEvent or MultipleBoundaryEvent or MessageBoundaryEvent
                or SignalBoundaryEvent or ConditionalBoundaryEvent;

    public IReadOnlyList<IInfrastructureEffect> HandleTimerFired(
        string timerActivityId, Guid hostActivityInstanceId)
    {
        timerActivityId = CurrentActivityId(timerActivityId);
        if (IsUnknownActivity(timerActivityId))
            return [];

        // Event sub-process timer path: the timer's "host" is either a SubProcess
        // scope container entry or the workflow instance id (_state.Id) for root-
        // scope event sub-processes. Detect via definition lookup and dispatch
        // before the regular stale-entry guard (the root host id has no entry).
        var espMatch = _definition.FindEventSubProcessByStartEvent(timerActivityId);
        if (espMatch is not null && espMatch.Value.EventSubProcess.Activities
                .OfType<TimerStartEvent>()
                .Any(t => t.ActivityId == timerActivityId))
        {
            return TryActivateTimerEventSubProcess(
                espMatch.Value.EventSubProcess,
                espMatch.Value.EnclosingScope,
                hostActivityInstanceId);
        }

        // Stale guard: if entry is no longer active, ignore
        var entry = _state.FindEntry(hostActivityInstanceId);
        if (entry is null || entry.IsCompleted || IsIterationAsBoundaryHost(timerActivityId, entry))
            return [];

        // Look up the activity type in the definition
        var activity = _definition.GetActivityAcrossScopes(timerActivityId);

        if (activity is BoundaryTimerEvent boundaryTimer)
        {
            // Skip the fired timer: its TimerCallbackGrain is the (non-reentrant) caller of
            // this method and unregisters its own reminder once we return. Emitting an
            // UnregisterTimerEffect for it would call back into that busy grain and deadlock
            // until the Orleans response timeout (#759).
            return HandleBoundaryEventFired(
                boundaryTimer, boundaryTimer.AttachedToActivityId,
                boundaryTimer.IsInterrupting, entry, new ExpandoObject(),
                skipTimerActivityId: boundaryTimer.ActivityId,
                skipMessageName: null,
                skipSignalName: null);
        }

        if (activity is MultipleBoundaryEvent multiBoundaryTimer)
        {
            return HandleBoundaryEventFired(
                multiBoundaryTimer, multiBoundaryTimer.AttachedToActivityId,
                multiBoundaryTimer.IsInterrupting, entry, new ExpandoObject(),
                skipTimerActivityId: multiBoundaryTimer.ActivityId,
                skipMessageName: null,
                skipSignalName: null);
        }

        // Multiple intermediate catch timer: cancel siblings, then complete
        if (activity is MultipleIntermediateCatchEvent multiCatchTimer)
        {
            var effects = new List<IInfrastructureEffect>();
            effects.AddRange(CancelMultipleEventSiblings(
                multiCatchTimer, entry,
                skipMessageName: null, skipSignalName: null,
                skipTimerActivityId: timerActivityId));
            effects.AddRange(CompleteActivity(timerActivityId, hostActivityInstanceId, new ExpandoObject()));
            return effects.AsReadOnly();
        }

        // Intermediate catch timer: complete the activity with empty variables
        return CompleteActivity(timerActivityId, hostActivityInstanceId, new ExpandoObject());
    }

    public IReadOnlyList<IInfrastructureEffect> HandleMessageDelivery(
        string activityId, Guid hostActivityInstanceId, ExpandoObject variables)
    {
        activityId = CurrentActivityId(activityId);
        if (IsUnknownActivity(activityId))
            return [];

        // Event sub-process message path: the delivered message targets a
        // MessageStartEvent inside an EventSubProcess. Route before the
        // stale-entry guard (the root-scope host id has no matching entry).
        var espMatch = _definition.FindEventSubProcessByStartEvent(activityId);
        if (espMatch is not null && espMatch.Value.EventSubProcess.Activities
                .OfType<MessageStartEvent>()
                .Any(m => m.ActivityId == activityId))
        {
            return TryActivateMessageEventSubProcess(
                espMatch.Value.EventSubProcess,
                espMatch.Value.EnclosingScope,
                hostActivityInstanceId,
                variables);
        }

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

        if (activity is MultipleBoundaryEvent multiBoundaryMsg)
        {
            // Message subscription was already removed by the correlation grain.
            // HandleBoundaryEventFired + boundary unsubscribe will clean up remaining watchers.
            return HandleBoundaryEventFired(
                multiBoundaryMsg, multiBoundaryMsg.AttachedToActivityId,
                multiBoundaryMsg.IsInterrupting, entry, variables,
                skipTimerActivityId: null,
                skipMessageName: null,
                skipSignalName: null);
        }

        // Multiple intermediate catch message: cancel siblings, then complete.
        // The fired message's subscription was already removed by the correlation grain.
        // We unsubscribe all sibling watchers (including other messages — the correlation
        // grain's Unsubscribe is idempotent for the already-cleared one).
        if (activity is MultipleIntermediateCatchEvent multiCatchMsg)
        {
            var effects = new List<IInfrastructureEffect>();
            effects.AddRange(CancelMultipleEventSiblings(
                multiCatchMsg, entry,
                skipMessageName: null,
                skipSignalName: null,
                skipTimerActivityId: null));
            effects.AddRange(CompleteActivity(activityId, hostActivityInstanceId, variables));
            return effects.AsReadOnly();
        }

        // Intermediate catch message: complete the activity with delivered variables
        return CompleteActivity(activityId, hostActivityInstanceId, variables);
    }

    public IReadOnlyList<IInfrastructureEffect> HandleSignalDelivery(
        string activityId, Guid hostActivityInstanceId)
    {
        activityId = CurrentActivityId(activityId);
        if (IsUnknownActivity(activityId))
            return [];

        // Event sub-process signal path: the delivered signal targets a
        // SignalStartEvent inside an EventSubProcess. Route before the
        // stale-entry guard (the root-scope host id has no matching entry).
        var espMatch = _definition.FindEventSubProcessByStartEvent(activityId);
        if (espMatch is not null && espMatch.Value.EventSubProcess.Activities
                .OfType<SignalStartEvent>()
                .Any(s => s.ActivityId == activityId))
        {
            return TryActivateSignalEventSubProcess(
                espMatch.Value.EventSubProcess,
                espMatch.Value.EnclosingScope,
                hostActivityInstanceId);
        }

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

        if (activity is MultipleBoundaryEvent multiBoundarySignal)
        {
            // Signal subscription was already removed by the signal correlation grain.
            return HandleBoundaryEventFired(
                multiBoundarySignal, multiBoundarySignal.AttachedToActivityId,
                multiBoundarySignal.IsInterrupting, entry, new ExpandoObject(),
                skipTimerActivityId: null,
                skipMessageName: null,
                skipSignalName: null);
        }

        // Multiple intermediate catch signal: cancel siblings, then complete
        if (activity is MultipleIntermediateCatchEvent multiCatchSignal)
        {
            var effects = new List<IInfrastructureEffect>();
            effects.AddRange(CancelMultipleEventSiblings(
                multiCatchSignal, entry,
                skipMessageName: null,
                skipSignalName: null,
                skipTimerActivityId: null));
            effects.AddRange(CompleteActivity(activityId, hostActivityInstanceId, new ExpandoObject()));
            return effects.AsReadOnly();
        }

        // Intermediate catch signal: complete the activity with empty variables
        return CompleteActivity(activityId, hostActivityInstanceId, new ExpandoObject());
    }

    /// <summary>
    /// Fires a conditional watcher: completes the conditional intermediate catch event
    /// or fires the conditional boundary event.
    /// </summary>
    public IReadOnlyList<IInfrastructureEffect> FireConditionalWatcher(Guid activityInstanceId, string watcherActivityId)
    {
        var entry = _state.FindEntry(activityInstanceId);
        if (entry == null || entry.IsCompleted) return Array.Empty<IInfrastructureEffect>();

        Emit(new ConditionalWatcherFired(activityInstanceId));

        var activity = _definition.GetActivityAcrossScopes(watcherActivityId);
        if (activity is ConditionalBoundaryEvent boundary)
        {
            var hostEntry = _state.GetActiveActivities()
                .FirstOrDefault(e => e.ActivityId == boundary.AttachedToActivityId && !e.IsCompleted);
            if (hostEntry == null) return Array.Empty<IInfrastructureEffect>();

            return HandleBoundaryEventFired(
                boundary, boundary.AttachedToActivityId,
                boundary.IsInterrupting, hostEntry, new ExpandoObject(),
                skipTimerActivityId: null,
                skipMessageName: null,
                skipSignalName: null);
        }

        // Intermediate catch: complete with empty variables
        return CompleteActivity(entry.ActivityId, activityInstanceId, new ExpandoObject());
    }

    /// <summary>
    /// Clears a single conditional watcher by its activity instance ID.
    /// Used by BuildBoundaryUnsubscribeEffects for per-activity cleanup.
    /// </summary>
    public void ClearConditionalWatcher(Guid activityInstanceId)
    {
        Emit(new ConditionalWatcherCleared(activityInstanceId));
    }

    /// <summary>
    /// Updates the LastEvaluatedResult for a conditional watcher (edge-triggered tracking).
    /// </summary>
    public void UpdateConditionalWatcherResult(Guid activityInstanceId, bool result)
    {
        Emit(new ConditionalWatcherResultUpdated(activityInstanceId, result));
    }

    // --- Scope Completion ---

    /// <summary>
    /// Detects when all entries in a subprocess or multi-instance scope are completed.
    /// Emits ActivityCompleted for completed scope hosts.
    /// Returns effects (e.g., boundary unsubscribe) and the list of completed host instance IDs
    /// so the grain can compute transitions for them via ResolveTransitions.
    /// </summary>
    public (IReadOnlyList<IInfrastructureEffect> Effects, IReadOnlyList<Guid> CompletedHostInstanceIds, IReadOnlyList<Guid> OrphanedScopeIds)
        CompleteFinishedSubProcessScopes()
    {
        var allEffects = new List<IInfrastructureEffect>();
        var allCompletedHostIds = new List<Guid>();
        var allOrphanedScopeIds = new List<Guid>();
        // Event sub-processes have no outgoing sequence flows, so when a root-scope
        // EventSubProcess completes we must emit WorkflowCompleted directly — the
        // normal path (outgoing flow → root EndEvent → CompleteWorkflowCommand) is
        // unavailable.
        var completedRootEventSubProcess = false;

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

                // A multi-instance iteration shares the host's ActivityId, so the lookup
                // returns the MultiInstanceActivity wrapper. Unwrap it so an iteration of a
                // multi-instance SubProcess is completed like a regular SubProcess once its
                // body finishes (otherwise the iteration — and therefore the host — never
                // completes).
                var isMultiInstanceIteration = false;
                if (activity is MultiInstanceActivity iterationWrapper && entry.MultiInstanceIndex is not null)
                {
                    activity = iterationWrapper.InnerActivity;
                    isMultiInstanceIteration = true;
                }

                var isSubProcess = activity is SubProcess;
                var isEventSubProcess = activity is EventSubProcess;
                var isMultiInstanceHost = activity is MultiInstanceActivity
                    && entry.MultiInstanceIndex is null;

                if (!isSubProcess && !isMultiInstanceHost && !isEventSubProcess) continue;

                var scopeEntries = _state.GetEntriesInScope(entry.ActivityInstanceId);

                if (scopeEntries.Count == 0 && !isMultiInstanceHost) continue;

                if (isMultiInstanceHost)
                {
                    var mi = (MultiInstanceActivity)activity;
                    var miResult = _multiInstance.TryComplete(entry, mi, scopeEntries,
                        deferSequentialSpawn: mi.IsSequential && mi.CompletionCondition is not null);
                    if (miResult.AwaitingCompletionCondition)
                    {
                        // The condition's result decides between completing the host and the next
                        // iteration (ContinueSequentialMultiInstance).
                        var completedCount = scopeEntries.Count(e => e.IsCompleted);
                        allEffects.Add(new PublishDomainEventEffect(new EvaluateCompletionConditionEvent(
                            _state.Id,
                            _definition.WorkflowId,
                            _definition.ProcessDefinitionId,
                            entry.ActivityInstanceId,
                            mi.ActivityId,
                            mi.CompletionCondition!,
                            entry.MultiInstanceTotal ?? 0,
                            0,
                            completedCount)));
                    }
                    else if (miResult.HostCompleted)
                    {
                        var boundaryEffects = BuildBoundaryUnsubscribeEffects(miResult.HostActivityId!, entry);
                        allEffects.AddRange(boundaryEffects);
                        allCompletedHostIds.Add(entry.ActivityInstanceId);
                        anyCompleted = true;
                    }
                    else if (mi.CompletionCondition is not null && !mi.IsSequential)
                    {
                        // Re-query scope after any sequential spawn to get current counts
                        var freshScope = _state.GetEntriesInScope(entry.ActivityInstanceId);
                        var activeCount = freshScope.Count(e => !e.IsCompleted);
                        var completedCount = freshScope.Count(e => e.IsCompleted);
                        if (activeCount > 0 && completedCount > 0)
                        {
                            allEffects.Add(new PublishDomainEventEffect(new EvaluateCompletionConditionEvent(
                                _state.Id,
                                _definition.WorkflowId,
                                _definition.ProcessDefinitionId,
                                entry.ActivityInstanceId,
                                mi.ActivityId,
                                mi.CompletionCondition,
                                entry.MultiInstanceTotal ?? 0,
                                activeCount,
                                completedCount)));
                        }
                    }
                    continue;
                }

                // SubProcess: all scope children must be completed
                if (!scopeEntries.All(e => e.IsCompleted)) continue;

                // A nested scope host completed earlier in THIS call has not had its outgoing
                // transitions resolved yet (the caller resolves them after we return), so its
                // token is still in flight inside this scope. Cascading now would complete the
                // enclosing scope before its post-nested-scope activities run — e.g. an outer
                // Transaction committing while its catch event after the inner Transaction has
                // not even been spawned (#761). Only cascade through hosts with no outgoing flow.
                if (scopeEntries.Any(e => allCompletedHostIds.Contains(e.ActivityInstanceId)
                        && HostRoutesTokenAfterCompletion(e)))
                    continue;

                // If any scope child has an error (and wasn't handled by a boundary),
                // a regular SubProcess should NOT auto-complete — its boundary error
                // events catch the failure and route execution. EventSubProcesses have
                // no boundary events, so an unhandled handler failure must still close
                // the scope (the failure stays visible on the failed child entry); the
                // ESP host will be marked completed below so the workflow can terminate.
                // A failure an error event sub-process of this scope caught is handled: the
                // handler's end closes the scope like any other path (#1123).
                if (isSubProcess && scopeEntries.Any(e => e.ErrorCode is not null)
                    && !scopeEntries.Any(IsErrorEventSubProcessEntry)) continue;

                // Message ESP listeners in a sub-process are keyed on its own scope.
                var ownScopeForKeys = _state.ScopeHostExecutionScopes.TryGetValue(entry.ActivityInstanceId, out var keyScopeId)
                    ? keyScopeId
                    : entry.VariablesId;

                ExpandoObject? compensationSnapshot = null;
                if (isSubProcess)
                {
                    // SubProcess: merge the host's OWN execution scope into the parent
                    // before completing. Ownership is recorded when the scope opens
                    // (ScopeHostExecutionScopeOpened); other scopes that share its parent
                    // (branch clones, iteration scopes, other hosts' scopes) are not ours.
                    WorkflowVariablesState? ownExecutionScope =
                        _state.ScopeHostExecutionScopes.TryGetValue(entry.ActivityInstanceId, out var ownId)
                            ? _state.VariableStates.FirstOrDefault(s => s.Id == ownId)
                            : null;

                    // With output mappings only their values reach the parent scope.
                    var mappedActivity = FindMappedActivity(entry);
                    ExpandoObject? mappedOutputs = null;
                    if (mappedActivity?.OutputMappings.Count > 0)
                    {
                        try
                        {
                            mappedOutputs = MapOutputs(entry, new ExpandoObject(),
                                ownExecutionScope?.Id ?? entry.ExecutionVariablesId);
                        }
                        catch (MappingEvaluationException ex)
                        {
                            allEffects.AddRange(FailActivity(entry.ActivityId, entry.ActivityInstanceId, ex));
                            continue;
                        }
                        Emit(new VariablesMerged(entry.VariablesId, mappedOutputs));
                    }

                    compensationSnapshot = mappedOutputs;
                    if (ownExecutionScope is not null)
                    {
                        if (mappedOutputs is null)
                        {
                            compensationSnapshot = mappedActivity is null
                                ? ownExecutionScope.Variables
                                : WithoutLocalInputs(mappedActivity, ownExecutionScope.Variables);
                            Emit(new VariablesMerged(entry.VariablesId, compensationSnapshot));
                        }
                        // Defer scope removal: nested subprocess transitions (resolved after
                        // this method returns) may still reference the child scope.
                        allOrphanedScopeIds.Add(ownExecutionScope.Id);
                    }

                    // The input scope goes with the own scope it parents.
                    if (entry.InputScopeId is Guid inputScopeId)
                        allOrphanedScopeIds.Add(inputScopeId);
                }
                else if (isEventSubProcess)
                {
                    // EventSubProcess: discard the handler scope it owns, no merge back.
                    // Other scopes under the same parent belong to live siblings (#840).
                    if (_state.ScopeHostExecutionScopes.TryGetValue(entry.ActivityInstanceId, out var handlerScopeId))
                        allOrphanedScopeIds.Add(handlerScopeId);
                }

                // Record Completed outcome for Transaction Sub-Process hosts unless the cancel or
                // hazard flow already set a different outcome (cancel path runs before this method).
                if (activity is Transaction
                    && (!_state.TransactionOutcomes.TryGetValue(entry.ActivityInstanceId, out var priorOutcome)
                        || priorOutcome.Outcome == TransactionOutcome.Completed))
                    SetTransactionOutcomeCompleted(entry.ActivityInstanceId);

                // A completed sub-process with a compensation boundary is compensable as a
                // whole (BPMN §10.6): its snapshot is what reached the parent scope (#1124).
                if (isSubProcess)
                    RecordCompensationSnapshotIfCompensable(entry, compensationSnapshot ?? new ExpandoObject());

                // All scope children are done — complete the sub-process host.
                // Variables arg is empty because the merge already happened above.
                Emit(new ActivityCompleted(
                    entry.ActivityInstanceId, entry.VariablesId, new ExpandoObject()));

                // Boundaries belong to the multi-instance host, not to its iterations —
                // they are unsubscribed when the host completes (TryComplete path above).
                if (!isMultiInstanceIteration)
                {
                    var effects = BuildBoundaryUnsubscribeEffects(entry.ActivityId, entry);
                    allEffects.AddRange(effects);
                }

                // Unregister any event-sub-process timers declared inside this
                // completing scope (SubProcess only — event sub-processes don't
                // nest their own event-sub timers in slice #C).
                if (isSubProcess)
                {
                    var subDef = (IWorkflowDefinition)(SubProcess)activity;
                    allEffects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
                        subDef, entry.ActivityInstanceId,
                        scopeVariablesId: ownScopeForKeys,
                        skipStartEventActivityId: null));
                }

                allCompletedHostIds.Add(entry.ActivityInstanceId);
                anyCompleted = true;

                if (isEventSubProcess && entry.ScopeId is null)
                    completedRootEventSubProcess = true;
            }
        } while (anyCompleted);

        // If a root-scope EventSubProcess just completed and nothing else is active,
        // finalize the workflow here — no outgoing flow exists to carry the token to
        // a root EndEvent.
        if (completedRootEventSubProcess
            && !_state.IsCompleted
            && !_state.GetActiveActivities().Any())
        {
            // Unregister any still-armed root-scope event sub-process listeners.
            allEffects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
                _definition, scopeContainerId: null,
                scopeVariablesId: _state.GetRootVariablesId(),
                skipStartEventActivityId: null));

            Emit(new WorkflowCompleted());
            if (_state.ParentWorkflowInstanceId.HasValue)
            {
                var rootVariables = _state.GetMergedVariables(_state.GetRootVariablesId());
                allEffects.Add(new NotifyParentCompletedEffect(
                    _state.ParentWorkflowInstanceId.Value,
                    _state.ParentActivityId!,
                    rootVariables));
            }
        }

        return (allEffects.AsReadOnly(), allCompletedHostIds.AsReadOnly(), allOrphanedScopeIds.AsReadOnly());
    }

    private bool IsErrorEventSubProcessEntry(ActivityInstanceEntry entry)
        => _definition.GetActivityAcrossScopes(entry.ActivityId) is EventSubProcess esp
            && esp.Activities.OfType<ErrorStartEvent>().Any();

    /// <summary>
    /// True when a just-completed scope host still routes a token inside its enclosing scope:
    /// either along an outgoing sequence flow, or — for a Cancelled Transaction — through its
    /// attached Cancel Boundary Event (activated by the caller after this pass).
    /// </summary>
    private bool HostRoutesTokenAfterCompletion(ActivityInstanceEntry hostEntry)
    {
        var scope = _definition.GetScopeForActivity(hostEntry.ActivityId);
        if (scope.SequenceFlows.Any(sf => sf.Source.ActivityId == hostEntry.ActivityId))
            return true;

        return _state.TransactionOutcomes.TryGetValue(hostEntry.ActivityInstanceId, out var outcome)
            && outcome.Outcome == TransactionOutcome.Cancelled
            && scope.Activities.OfType<CancelBoundaryEvent>()
                .Any(b => b.AttachedToActivityId == hostEntry.ActivityId);
    }

    // --- User Task Handling ---

    /// <summary>
    /// Claims a custom task for one dispatch (#818), so that of two dispatcher activations running
    /// the same delivery only one hands it to a worker. <paramref name="lease"/> is how long the
    /// claim holds unless claimed again by the same dispatch; <c>null</c> holds it until the
    /// activity is settled (a deferred task). A claim by the same dispatch renews it, journaling a
    /// new lease only once less than half of the current one is left.
    /// </summary>
    public CustomTaskClaimResult ClaimCustomTask(Guid activityInstanceId, Guid dispatchId, TimeSpan? lease)
    {
        var entry = _state.FindEntry(activityInstanceId);
        if (entry is null || entry.IsCompleted)
            return CustomTaskClaimResult.Finished;

        var now = DateTimeOffset.UtcNow;
        if (entry.IsCustomTaskClaimedByOther(dispatchId, now))
            return entry.CustomTaskClaimUntil is null ? CustomTaskClaimResult.Deferred : CustomTaskClaimResult.Held;

        // A renewal while more than half of the lease is left changes nothing worth a journal
        // event: the holder renews well before the rest runs out.
        if (entry.CustomTaskClaimId == dispatchId && lease is { } renewal
            && entry.CustomTaskClaimUntil is { } until && until - now > renewal / 2)
            return CustomTaskClaimResult.Granted;

        Emit(new CustomTaskClaimed(activityInstanceId, dispatchId, now + lease));
        return CustomTaskClaimResult.Granted;
    }

    /// <summary>Gives up a custom task's claim, so its redelivery need not wait for the lease. A no-op unless <paramref name="dispatchId"/> holds it.</summary>
    public void ReleaseCustomTaskClaim(Guid activityInstanceId, Guid dispatchId)
    {
        if (_state.FindEntry(activityInstanceId) is { IsCompleted: false } entry && entry.CustomTaskClaimId == dispatchId)
            Emit(new CustomTaskClaimReleased(activityInstanceId, dispatchId));
    }

    public IReadOnlyList<IInfrastructureEffect> ClaimUserTask(
        Guid activityInstanceId, string userId, IReadOnlyList<string> userGroups)
    {
        return _userTasks.Claim(activityInstanceId, userId, userGroups);
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

    public IReadOnlyList<IInfrastructureEffect> FailUserTask(
        Guid activityInstanceId, string errorCode, string errorMessage)
    {
        var metadata = _state.UserTasks.GetValueOrDefault(activityInstanceId);
        // Idempotency: already in a terminal state or already cleaned up (a completed task is
        // unregistered, so it is null here)
        if (metadata is null || metadata.TaskState
            is UserTaskLifecycleState.Failed
            or UserTaskLifecycleState.Cancelled)
            return [];

        var entry = _state.GetActiveEntry(activityInstanceId);
        Emit(new UserTaskFailed(entry.ActivityInstanceId, entry.ActivityId, errorCode, errorMessage));

        return FailActivity(entry.ActivityId, activityInstanceId,
            new CustomTaskFailedActivityException(errorCode, errorMessage));
    }

    public IReadOnlyList<IInfrastructureEffect> CancelUserTask(
        Guid activityInstanceId, string? reason)
    {
        var metadata = _state.UserTasks.GetValueOrDefault(activityInstanceId);
        // Idempotency: already in a terminal state or already cleaned up (a completed task is
        // unregistered, so it is null here)
        if (metadata is null || metadata.TaskState
            is UserTaskLifecycleState.Failed
            or UserTaskLifecycleState.Cancelled)
            return [];

        var entry = _state.GetActiveEntry(activityInstanceId);
        Emit(new UserTaskCancelled(entry.ActivityInstanceId, entry.ActivityId, reason));
        Emit(new ActivityCancelled(entry.ActivityInstanceId, reason ?? "User task cancelled by operator"));

        var effects = new List<IInfrastructureEffect>();
        effects.AddRange(BuildUserTaskCleanupEffects(entry.ActivityInstanceId));
        return effects;
    }

    private List<IInfrastructureEffect> BuildUserTaskCleanupEffects(Guid activityInstanceId)
    {
        if (_state.UserTasks.ContainsKey(activityInstanceId))
        {
            Emit(new UserTaskUnregistered(activityInstanceId));
            // Called after the entry's terminal event: the task record ends the way the entry did.
            var entry = _state.FindEntry(activityInstanceId);
            var finalState = entry switch
            {
                { IsCancelled: true } => UserTaskLifecycleState.Cancelled,
                { ErrorCode: not null } => UserTaskLifecycleState.Failed,
                _ => UserTaskLifecycleState.Completed,
            };
            return [new CompleteUserTaskPersistenceEffect(activityInstanceId, finalState)];
        }
        return [];
    }

    // --- Multi-Instance Early Completion ---

    public IReadOnlyList<IInfrastructureEffect> CompleteMultiInstanceEarly(
        string hostActivityId, Guid hostActivityInstanceId)
    {
        var entry = _state.FindEntry(hostActivityInstanceId);
        // Stale guard: host already completed (normal path or prior early-completion callback)
        if (entry is null || entry.IsCompleted || entry.ActivityId != hostActivityId)
            return [];

        var effects = new List<IInfrastructureEffect>();

        // Cancel remaining active child iterations
        effects.AddRange(CancelScopeChildren(hostActivityInstanceId));

        var mi = (MultiInstanceActivity)_definition.GetActivityAcrossScopes(hostActivityId);
        _multiInstance.CompleteHost(entry, mi, _state.GetEntriesInScope(hostActivityInstanceId).ToList());

        effects.AddRange(BuildBoundaryUnsubscribeEffects(hostActivityId, entry));

        return effects;
    }

    /// <summary>
    /// A sequential multi-instance activity's completion condition came out false: start the next
    /// iteration (#1126). Only when the host still waits exactly where the evaluation saw it — no
    /// iteration running and <paramref name="completedAtEvaluation"/> completed — so a repeated
    /// result starts nothing twice.
    /// </summary>
    public void ContinueSequentialMultiInstance(Guid hostActivityInstanceId, int completedAtEvaluation)
    {
        var host = _state.FindEntry(hostActivityInstanceId);
        if (host is null || host.IsCompleted || host.MultiInstanceTotal is not { } total)
            return;
        var iterations = _state.GetEntriesInScope(hostActivityInstanceId);
        if (iterations.Any(e => !e.IsCompleted)) return;
        var completed = iterations.Count(e => e.IsCompleted);
        if (completed != completedAtEvaluation || completed >= total) return;

        var mi = (MultiInstanceActivity)_definition.GetActivityAcrossScopes(host.ActivityId);
        _multiInstance.SpawnNextSequentialIteration(host, mi, completed);
    }

    /// <summary>
    /// Fails a multi-instance host whose completion condition cannot be evaluated (#849): its
    /// running iterations are cancelled and the failure goes the usual way (boundary, event
    /// sub-process, or the instance).
    /// </summary>
    public IReadOnlyList<IInfrastructureEffect> FailMultiInstanceHost(Guid hostActivityInstanceId, Exception exception)
    {
        var host = _state.FindEntry(hostActivityInstanceId);
        if (host is null || host.IsCompleted)
            return [];
        var effects = CancelScopeChildren(hostActivityInstanceId,
            "Multi-instance activity failed: its completion condition could not be evaluated");
        effects.AddRange(FailActivity(host.ActivityId, hostActivityInstanceId, exception));
        return effects;
    }

    // --- Condition Sequence Handling ---

    public void EvaluateConditionSequence(Guid activityInstanceId, string sequenceId, bool result)
    {
        Emit(new ConditionSequenceEvaluated(activityInstanceId, sequenceId, result));
    }

    private void CreateOrIncrementComplexGatewayJoinToken(ActivityInstanceEntry joinEntry, string activationCondition)
    {
        var existing = _state.GetComplexGatewayJoinState(joinEntry.ActivityId, joinEntry.ScopeId);
        if (existing is null)
            Emit(new ComplexGatewayJoinStateCreated(joinEntry.ActivityId, joinEntry.ActivityInstanceId,
                activationCondition, _state.Id, joinEntry.ScopeId));
        Emit(new ComplexGatewayJoinStateTokenIncremented(joinEntry.ActivityId, joinEntry.ScopeId));
    }

    /// <summary>The join state of the scope the join entry runs in (#839).</summary>
    public ComplexGatewayJoinState? GetComplexGatewayJoinStateFor(Guid joinActivityInstanceId)
        => _state.FindEntry(joinActivityInstanceId) is { } entry
            ? _state.GetComplexGatewayJoinState(entry.ActivityId, entry.ScopeId)
            : null;

    /// <summary>
    /// Fires the complex join of <paramref name="joinActivityInstanceId"/>'s scope and completes its
    /// first entry. The state stays, marked fired, so the fork's remaining tokens are discarded as
    /// late; it is removed once they have all arrived, so a later pass (a loop, a re-entered scope)
    /// gets a fresh join (#839).
    /// </summary>
    public void FireComplexGatewayJoin(Guid joinActivityInstanceId)
    {
        var joinEntry = _state.GetEntry(joinActivityInstanceId);
        var joinState = _state.GetComplexGatewayJoinState(joinEntry.ActivityId, joinEntry.ScopeId)
            ?? throw new InvalidOperationException($"ComplexGatewayJoinState not found for gateway '{joinEntry.ActivityId}'");
        var firstEntry = _state.GetEntry(joinState.FirstActivityInstanceId);
        int? expected = firstEntry.TokenId is { } tokenId && _state.FindForkByToken(tokenId) is { } fork
            ? fork.CreatedTokenIds.Count
            : null;

        Emit(new ComplexGatewayJoinStateFired(joinEntry.ActivityId, joinEntry.ScopeId, expected));
        Emit(new ActivityCompleted(joinState.FirstActivityInstanceId, firstEntry.VariablesId, new ExpandoObject()));
        RemoveComplexGatewayJoinIfDone(joinEntry.ActivityId, joinEntry.ScopeId);
    }

    private void RemoveComplexGatewayJoinIfDone(string gatewayActivityId, Guid? scopeId)
    {
        if (_state.GetComplexGatewayJoinState(gatewayActivityId, scopeId) is { AllTokensArrived: true })
            Emit(new ComplexGatewayJoinStateRemoved(gatewayActivityId, scopeId));
    }

    /// <summary>
    /// Records one gateway condition's result on the gateway instance it was evaluated for — never
    /// another live instance of the same gateway, e.g. a parallel multi-instance iteration's (#844).
    /// </summary>
    public void CompleteConditionSequence(
        string activityId, Guid activityInstanceId, string conditionSequenceId, bool result)
    {
        var entry = _state.FindEntry(activityInstanceId);
        if (entry is null || entry.IsCompleted || entry.ActivityId != activityId)
            throw new InvalidOperationException(
                $"No active entry '{activityInstanceId}' found for activity '{activityId}'.");

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
        if (gateway is InclusiveGateway or ComplexGateway)
        {
            // InclusiveGateway / ComplexGateway fork: wait for ALL conditions to be evaluated, never short-circuit
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
        // The gateway's own scope: a gateway inside a sub-process has its flows there.
        var hasDefault = _definition.GetScopeForActivity(activityId).SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .Any(sf => sf.Source.ActivityId == activityId);

        if (!hasDefault)
            throw new InvalidOperationException(
                $"{gatewayType} {activityId}: all conditions evaluated to false and no default flow exists");

        return true;
    }

    // --- Child Workflow Handling ---

    /// <summary>
    /// The live call-activity instance that started <paramref name="childInstanceId"/>. Iterations of
    /// a multi-instance call activity share the activity id, so the child's id is the only key that
    /// names one of them (#782). Null once that call activity has ended.
    /// </summary>
    private ActivityInstanceEntry? FindActiveCallActivityOfChild(Guid childInstanceId)
        => _state.GetActiveActivities().FirstOrDefault(e => e.ChildWorkflowInstanceId == childInstanceId);

    public IReadOnlyList<IInfrastructureEffect> OnChildWorkflowCompleted(
        Guid childInstanceId, string parentActivityId, ExpandoObject childVariables)
    {
        parentActivityId = CurrentActivityId(parentActivityId);
        // Stale guard: if the call activity is no longer active, ignore
        var entry = FindActiveCallActivityOfChild(childInstanceId);
        if (entry is null)
            return [];

        var activity = _definition.GetActivityAcrossScopes(entry.ActivityId);
        // A multi-instance iteration resolves to its host's wrapper; the iteration runs the inner one.
        if (activity is MultiInstanceActivity mi && entry.MultiInstanceIndex is not null)
            activity = mi.InnerActivity;
        var callActivity = activity as CallActivity
            ?? throw new InvalidOperationException(
                $"Activity '{parentActivityId}' is not a CallActivity.");

        var mappedOutput = callActivity.BuildParentOutputVariables(childVariables);
        return CompleteActivity(parentActivityId, entry.ActivityInstanceId, mappedOutput);
    }

    public IReadOnlyList<IInfrastructureEffect> OnChildWorkflowFailed(
        Guid childInstanceId, string parentActivityId, Exception exception)
    {
        parentActivityId = CurrentActivityId(parentActivityId);
        // Stale guard: if the call activity is no longer active, ignore
        var entry = FindActiveCallActivityOfChild(childInstanceId);
        if (entry is null)
            return [];

        return FailActivity(parentActivityId, entry.ActivityInstanceId, exception);
    }

    public (IReadOnlyList<IInfrastructureEffect> Effects, EscalationHandledResult Result)
        HandleChildEscalationRaised(
            Guid escalationInstanceId,
            Guid childWorkflowInstanceId,
            string hostActivityId,
            string escalationCode,
            ExpandoObject variables)
    {
        hostActivityId = CurrentActivityId(hostActivityId);
        // Find the host activity entry (CallActivity/SubProcess) in the parent
        var hostEntry = FindActiveCallActivityOfChild(childWorkflowInstanceId);
        if (hostEntry is null)
            return ([], EscalationHandledResult.Unhandled);

        // Use the definition's escalation boundary walker
        var boundaryHandler = _definition.FindBoundaryEscalationHandler(hostActivityId, escalationCode);

        // An escalation event sub-process inside the boundary's scope catches it first (#783).
        // Interrupting, it cancels the call activity with the rest of its scope.
        var espEffects = new List<IInfrastructureEffect>();
        if (TryActivateEscalationEventSubProcess(hostEntry, escalationCode, variables,
                boundaryHandler?.Scope, espEffects) is { } interrupting)
            return (espEffects, interrupting ? EscalationHandledResult.Cancelled : EscalationHandledResult.Continue);

        if (boundaryHandler is not null)
        {
            var (boundaryEvent, scope, attachedToActivityId) = boundaryHandler.Value;
            var attachedEntry = FindBoundaryHost(hostEntry, attachedToActivityId);

            // If the attached activity is no longer active (already completed/cancelled),
            // the escalation boundary should not fire.
            if (attachedEntry is null)
                return ([], EscalationHandledResult.Unhandled);

            return HandleEscalationBoundaryMatch(boundaryEvent, attachedEntry, variables);
        }

        // No boundary found in this grain — check if we have a parent (CallActivity escape)
        if (_state.ParentWorkflowInstanceId.HasValue)
        {
            var effects = new List<IInfrastructureEffect>
            {
                new NotifyParentEscalationRaisedEffect(
                    _state.ParentWorkflowInstanceId.Value,
                    _state.Id,
                    _state.ParentActivityId!,
                    escalationCode,
                    variables,
                    // Propagate the origin throw's id unchanged so a re-escalated hop keeps the
                    // same op-id — dedup short-circuits a retried hop (#657, round-4 fix).
                    EscalationInstanceId: escalationInstanceId)
            };
            return (effects, EscalationHandledResult.NeedsParentLookup);
        }

        // Uncaught at root
        Emit(new EscalationUncaughtRaised(escalationCode, hostActivityId));
        return ([], EscalationHandledResult.Unhandled);
    }

    /// <summary>
    /// Starts the escalation event sub-process that catches an escalation raised by
    /// <paramref name="fromEntry"/>, unless a boundary found in <paramref name="boundaryScope"/>
    /// is nearer: a boundary on a container is reached before the event sub-processes of the
    /// scope around it, an event sub-process inside the container before the boundary (#783).
    /// Returns whether the started event sub-process interrupts, or null when none was started.
    /// </summary>
    private bool? TryActivateEscalationEventSubProcess(
        ActivityInstanceEntry fromEntry, string escalationCode, ExpandoObject escalationVariables,
        IWorkflowDefinition? boundaryScope, List<IInfrastructureEffect> effects)
    {
        var match = _definition.FindEscalationEventSubProcessHandler(fromEntry.ActivityId, escalationCode);
        if (match is null) return null;

        var (eventSubProcess, enclosingScope) = match.Value;
        if (boundaryScope is not null && ScopeDepth(enclosingScope) <= ScopeDepth(boundaryScope))
            return null;

        var enclosingScopeContainerId = FindEnclosingScopeContainerId(fromEntry, enclosingScope);
        if (enclosingScopeContainerId is Guid containerId && _state.FindEntry(containerId) is not { IsCompleted: false })
            return null;
        var enclosingScopeVariablesId = ResolveEnclosingScopeVariablesId(enclosingScopeContainerId);
        var startEvent = eventSubProcess.Activities.OfType<EscalationStartEvent>()
            .First(s => s.EscalationCode == escalationCode || s.EscalationCode == null);

        SpawnEventSubProcessHandler(
            eventSubProcess, startEvent, nameof(EscalationStartEvent), enclosingScope,
            enclosingScopeContainerId, enclosingScopeVariablesId,
            // The escalation's variables reach the handler, as they reach an escalation boundary.
            deliveredVariables: escalationVariables, effects,
            cancelReason: $"Scope cancelled by escalation event sub-process '{eventSubProcess.ActivityId}'");
        return eventSubProcess.IsInterrupting;
    }

    /// <summary>Nesting depth of a definition scope: 0 for the process, 1 for a top-level sub-process.</summary>
    private int ScopeDepth(IWorkflowDefinition scope)
    {
        var depth = 0;
        while (!scope.IsRootScope && scope is Activity container)
        {
            depth++;
            scope = _definition.GetScopeForActivity(container.ActivityId);
        }
        return depth;
    }

    private (IReadOnlyList<IInfrastructureEffect> Effects, EscalationHandledResult Result)
        HandleEscalationBoundaryMatch(
            EscalationBoundaryEvent boundaryEvent,
            ActivityInstanceEntry hostEntry,
            ExpandoObject escalationVariables)
    {
        var effects = new List<IInfrastructureEffect>();

        if (boundaryEvent.IsInterrupting)
        {
            // Cancel host scope children
            effects.AddRange(CancelScopeChildren(hostEntry.ActivityInstanceId));

            // Cancel the host entry itself
            if (!hostEntry.IsCompleted)
            {
                Emit(new ActivityCancelled(
                    hostEntry.ActivityInstanceId,
                    $"Interrupted by escalation boundary event '{boundaryEvent.ActivityId}'"));
                effects.AddRange(BuildUserTaskCleanupEffects(hostEntry.ActivityInstanceId));
                effects.AddRange(BuildChildWorkflowCancelEffects(hostEntry));
            }

            // Unsubscribe all boundary subscriptions on the host
            effects.AddRange(BuildBoundaryUnsubscribeEffects(
                boundaryEvent.AttachedToActivityId, hostEntry));

            // Clone variables and merge escalation snapshot
            var clonedScopeId = Guid.NewGuid();
            Emit(new VariableScopeCloned(clonedScopeId, hostEntry.VariablesId, Detached: true));
            if (((IDictionary<string, object?>)escalationVariables).Count > 0)
                Emit(new VariablesMerged(clonedScopeId, escalationVariables));

            // Spawn the boundary event activity
            Emit(new ActivitySpawned(
                ActivityInstanceId: Guid.NewGuid(),
                ActivityId: boundaryEvent.ActivityId,
                ActivityType: boundaryEvent.GetType().Name,
                VariablesId: clonedScopeId,
                ScopeId: hostEntry.ScopeId,
                MultiInstanceIndex: null,
                TokenId: null));

            return (effects, EscalationHandledResult.Cancelled);
        }
        else
        {
            // Non-interrupting: clone variables and spawn boundary, host continues
            var clonedScopeId = Guid.NewGuid();
            Emit(new VariableScopeCloned(clonedScopeId, hostEntry.VariablesId, Detached: true));
            if (((IDictionary<string, object?>)escalationVariables).Count > 0)
                Emit(new VariablesMerged(clonedScopeId, escalationVariables));

            Emit(new ActivitySpawned(
                ActivityInstanceId: Guid.NewGuid(),
                ActivityId: boundaryEvent.ActivityId,
                ActivityType: boundaryEvent.GetType().Name,
                VariablesId: clonedScopeId,
                ScopeId: hostEntry.ScopeId,
                MultiInstanceIndex: null,
                TokenId: null));

            return (effects, EscalationHandledResult.Continue);
        }
    }

    /// <summary>
    /// Cancels the whole instance from outside: every active entry, at any depth, with the same
    /// cleanup a cancelled scope gets (subscriptions, user tasks, child instances), and the
    /// root-scope event sub-process listeners. Ends in <see cref="WorkflowCancelled"/>. No-op on
    /// an instance that already ended, so a redelivered cancel changes nothing.
    /// </summary>
    public IReadOnlyList<IInfrastructureEffect> CancelInstance(string reason)
    {
        if (!_state.IsStarted || _state.IsCompleted)
            return [];

        var effects = new List<IInfrastructureEffect>();
        foreach (var entry in _state.GetActiveActivities().Where(e => e.ScopeId is null).ToList())
        {
            if (_state.HasActiveChildrenInScope(entry.ActivityInstanceId))
                effects.AddRange(CancelScopeChildren(entry.ActivityInstanceId, reason));

            Emit(new ActivityCancelled(entry.ActivityInstanceId, reason));
            effects.AddRange(BuildCancelledEntryCleanupEffects(entry));
        }

        // Whatever the root pass didn't reach — an entry whose scope host already finished, such
        // as a compensation handler running in a completed sub-process's scope — is cancelled too,
        // and every compensation walk is aborted, so nothing stays active on a cancelled instance.
        foreach (var entry in _state.GetActiveActivities().ToList())
        {
            Emit(new ActivityCancelled(entry.ActivityInstanceId, reason));
            effects.AddRange(BuildCancelledEntryCleanupEffects(entry));
        }
        foreach (var walkScopeId in _state.ActiveCompensationWalks.Keys.ToList())
            AbortCompensationWalk(walkScopeId == Guid.Empty ? null : walkScopeId, "InstanceCancelled");

        effects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
            _definition, scopeContainerId: null,
            scopeVariablesId: _state.GetRootVariablesId(),
            skipStartEventActivityId: null));

        Emit(new WorkflowCancelled(reason));
        return effects.AsReadOnly();
    }

    public IReadOnlyList<IInfrastructureEffect> TerminateForParentEscalationCancellation()
        => CancelInstance("Escalation: child scope cancelled by parent interrupting boundary");

    private static (string Code, string Message) ToErrorCodeAndMessage(Exception exception)
    {
        if (exception is ActivityException activityException)
        {
            var errorState = activityException.GetActivityErrorState();
            return (errorState.Code, errorState.Message);
        }
        return ("500", exception.Message);
    }

    /// <summary>
    /// A root-scope event sub-process listener (timer, message or signal start) could not be
    /// registered. Its trigger would never arrive, so the instance fails rather than running on
    /// without it: active activities are cancelled, the other root listeners are unregistered,
    /// and <see cref="WorkflowFailed"/> names the start event. A stale repeat on a terminal
    /// instance is a no-op.
    /// </summary>
    private IReadOnlyList<IInfrastructureEffect> FailRootEventSubProcessListener(
        string startEventActivityId, Exception exception)
    {
        if (!_state.IsStarted || _state.IsCompleted)
            return [];

        var (errorCode, errorMessage) = ToErrorCodeAndMessage(exception);
        var effects = new List<IInfrastructureEffect>();

        foreach (var active in _state.GetActiveActivities().Where(e => e.ScopeId is null).ToList())
        {
            if (_state.HasActiveChildrenInScope(active.ActivityInstanceId))
                effects.AddRange(CancelScopeChildren(active.ActivityInstanceId));

            Emit(new ActivityCancelled(active.ActivityInstanceId,
                $"Event sub-process listener '{startEventActivityId}' registration failed"));
            effects.AddRange(BuildCancelledEntryCleanupEffects(active));
        }

        effects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
            _definition, scopeContainerId: null,
            scopeVariablesId: _state.GetRootVariablesId(),
            skipStartEventActivityId: startEventActivityId));

        Emit(new WorkflowFailed(startEventActivityId, _state.Id, errorCode, errorMessage));

        if (_state.ParentWorkflowInstanceId.HasValue)
            effects.Add(new NotifyParentFailedEffect(
                _state.ParentWorkflowInstanceId.Value, _state.ParentActivityId!, exception));

        return effects.AsReadOnly();
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

        // Stale guard: if entry is already completed (or pruned since), ignore stale callbacks
        if (entry is null || entry.IsCompleted)
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
        try
        {
            variables = MapOutputs(entry, variables ?? new ExpandoObject(), entry.ExecutionVariablesId);
        }
        catch (MappingEvaluationException ex)
        {
            return FailActivity(entry.ActivityId, entry.ActivityInstanceId, ex);
        }

        // Compensation snapshot hook: capture the activity's output variables at completion time.
        RecordCompensationSnapshotIfCompensable(entry, variables ?? new ExpandoObject());

        Emit(new ActivityCompleted(entry.ActivityInstanceId, entry.VariablesId, variables));
        RemoveInputScope(entry);

        var effects = new List<IInfrastructureEffect>();

        // Unsubscribe boundary subscriptions on this activity
        effects.AddRange(BuildBoundaryUnsubscribeEffects(entry.ActivityId, entry));

        // Cancel event-based gateway siblings
        effects.AddRange(CancelEventBasedGatewaySiblings(entry));

        // Clean up user task registry if applicable
        effects.AddRange(BuildUserTaskCleanupEffects(entry.ActivityInstanceId));

        return effects.AsReadOnly();
    }

    /// <param name="variables">
    /// Optional variables reported with the failure (Camunda's <c>bpmnError</c> variables), merged
    /// into the scope of whatever catches it: the failed activity's scope for an error boundary
    /// event (the boundary runs on it), the handler scope for an error event sub-process, and the
    /// failed activity's scope when nothing catches it. Not merged when the failure starts a
    /// transaction's cancellation, and not forwarded to a parent workflow's call activity.
    /// </param>
    public IReadOnlyList<IInfrastructureEffect> FailActivity(
        string activityId, Guid? activityInstanceId, Exception exception, ExpandoObject? variables = null)
    {
        // Root-scope event sub-process listeners are registered with the instance id as their
        // host — there is no activity entry to fail.
        if (activityInstanceId == _state.Id)
            return FailRootEventSubProcessListener(activityId, exception);

        var entry = ResolveEntry(activityId, activityInstanceId);

        // Stale guard: if entry is already completed (or pruned since), ignore stale callbacks
        if (entry is null || entry.IsCompleted)
            return [];

        if (variables is not null && ((IDictionary<string, object?>)variables).Count == 0)
            variables = null;

        var (errorCode, errorMessage) = ToErrorCodeAndMessage(exception);

        Emit(new ActivityFailed(entry.ActivityInstanceId, errorCode, errorMessage));

        var effects = new List<IInfrastructureEffect>();

        // Clean up user task registry if applicable
        effects.AddRange(BuildUserTaskCleanupEffects(entry.ActivityInstanceId));

        // Search for boundary error handler
        var boundaryHandler = _definition.FindBoundaryErrorHandler(activityId, errorCode);

        if (boundaryHandler is not null)
        {
            var (boundaryEvent, scope, attachedToActivityId) = boundaryHandler.Value;

            // Find the attached-to entry (may differ from the failed activity if
            // the boundary is on a parent subprocess)
            var attachedEntry = FindBoundaryHost(entry, attachedToActivityId);

            // Suppress premature Error Boundary fire when the boundary is on a Transaction host.
            // FindBoundaryErrorHandler() bubbles past TX scope boundaries and would fire immediately,
            // but BPMN §10.4.3 requires the compensation walk to complete first.
            // Comp walk handler failures emit CompensationWalkFailed (not FailActivity), so the
            // Hazard-state case here is defensive-only.
            // The same holds for any Transaction the error leaves on its way to the boundary: the
            // innermost one ends in Hazard first and its own failure travels on from there (#1122).
            if (FindInnermostOpenTransactionOnErrorPath(entry, attachedEntry) is { } hazardTx)
            {
                // Cancel remaining active TX scope siblings so all scope entries reach IsCompleted,
                // enabling InitiateTransactionHazardFlowIfNeeded() to detect the Hazard condition.
                effects.AddRange(CancelScopeChildren(hazardTx.ActivityInstanceId, "CancelledByHazard"));
                return effects.AsReadOnly();
            }

            // The boundary below is spawned on the failed entry's scope.
            if (variables is not null)
                Emit(new VariablesMerged(entry.VariablesId, variables));

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
                effects.AddRange(BuildChildWorkflowCancelEffects(attachedEntry));
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
        else if (TryActivateErrorEventSubProcess(entry, errorCode, effects, variables))
        {
            // Error event sub-process took over.
        }
        else
        {
            // No boundary handler found — keep the reported variables on the failed activity's
            // scope so they show in the instance state.
            if (variables is not null)
                Emit(new VariablesMerged(entry.VariablesId, variables));

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

    /// <summary>
    /// The live entry a boundary attached to <paramref name="attachedToActivityId"/> belongs to, as
    /// seen from <paramref name="fromEntry"/>: the entry itself or the nearest enclosing scope host
    /// with that id. A multi-instance iteration shares its host's id but is never the boundary host,
    /// so the walk continues to the host (#844). Null when no such live entry encloses it.
    /// </summary>
    private ActivityInstanceEntry? FindBoundaryHost(ActivityInstanceEntry fromEntry, string attachedToActivityId)
    {
        for (ActivityInstanceEntry? current = fromEntry; current is not null;
             current = current.ScopeId is { } scopeId ? _state.FindEntry(scopeId) : null)
        {
            if (current.ActivityId == attachedToActivityId && current.MultiInstanceIndex is null)
                return current.IsCompleted ? null : current;
        }
        return null;
    }

    /// <summary>
    /// The innermost Transaction, still without a Cancelled or Hazard outcome, that an error
    /// raised by <paramref name="failedEntry"/> leaves on its way to the boundary host
    /// <paramref name="boundaryHost"/> (inclusive). Null when the boundary sits on the failed
    /// activity itself or no such Transaction lies on the path.
    /// </summary>
    private ActivityInstanceEntry? FindInnermostOpenTransactionOnErrorPath(
        ActivityInstanceEntry failedEntry, ActivityInstanceEntry? boundaryHost)
    {
        if (boundaryHost is null || boundaryHost.ActivityInstanceId == failedEntry.ActivityInstanceId)
            return null;

        for (var scopeId = failedEntry.ScopeId; scopeId is { } id; )
        {
            var host = _state.FindEntry(id);
            if (host is null) return null;
            if (!host.IsCompleted
                && _definition.GetActivityAcrossScopes(host.ActivityId) is Activities.Transaction
                && (!_state.TransactionOutcomes.TryGetValue(host.ActivityInstanceId, out var outcome)
                    || outcome.Outcome == TransactionOutcome.Completed))
                return host;
            if (host.ActivityInstanceId == boundaryHost.ActivityInstanceId) return null;
            scopeId = host.ScopeId;
        }
        return null;
    }

    /// <summary>
    /// If an EventSubProcess with a matching ErrorStartEvent exists in an enclosing
    /// scope, spawns it (interrupting) and cancels siblings. Returns true if activated.
    /// Slice B: error trigger only. Peer timer/message/signal listener deregistration
    /// is a no-op here — slices #C–#E add the registration infrastructure those
    /// unsubscribes would target.
    /// </summary>
    private bool TryActivateErrorEventSubProcess(
        ActivityInstanceEntry failedEntry, string errorCode, List<IInfrastructureEffect> effects,
        ExpandoObject? failureVariables = null)
    {
        var match = _definition.FindErrorEventSubProcessHandler(
            failedEntry.ActivityId, errorCode);
        if (match is null) return false;

        var (eventSubProcess, enclosingScope) = match.Value;

        var enclosingScopeContainerId = FindEnclosingScopeContainerId(failedEntry, enclosingScope);
        var enclosingScopeVariablesId = ResolveEnclosingScopeVariablesId(
            enclosingScopeContainerId);

        // 1. Cancel active siblings in the enclosing scope. The failing entry is
        //    already terminal (ActivityFailed emitted) and will be filtered out.
        if (enclosingScopeContainerId.HasValue)
        {
            effects.AddRange(CancelScopeChildren(enclosingScopeContainerId.Value));
        }
        else
        {
            foreach (var sibling in _state.GetActiveActivities()
                .Where(e => e.ScopeId is null
                            && e.ActivityInstanceId != failedEntry.ActivityInstanceId)
                .ToList())
            {
                if (_state.HasActiveChildrenInScope(sibling.ActivityInstanceId))
                    effects.AddRange(CancelScopeChildren(sibling.ActivityInstanceId));

                Emit(new ActivityCancelled(
                    sibling.ActivityInstanceId,
                    $"Scope cancelled by error event sub-process '{eventSubProcess.ActivityId}'"));
                effects.AddRange(BuildCancelledEntryCleanupEffects(sibling));
            }
        }

        // 2. Spawn the EventSubProcess host as a new scope container.
        var espInstanceId = Guid.NewGuid();
        Emit(new ActivitySpawned(
            ActivityInstanceId: espInstanceId,
            ActivityId: eventSubProcess.ActivityId,
            ActivityType: nameof(EventSubProcess),
            VariablesId: enclosingScopeVariablesId,
            ScopeId: enclosingScopeContainerId,
            MultiInstanceIndex: null,
            TokenId: null));

        // 3. Child variable scope for the handler (isolated — not merged back).
        var handlerScopeId = Guid.NewGuid();
        Emit(new ChildVariableScopeCreated(handlerScopeId, enclosingScopeVariablesId));
        Emit(new ScopeHostExecutionScopeOpened(espInstanceId, handlerScopeId));

        // Variables reported with the failure go where the handler reads them: its own scope.
        // The failed activity's scope may be a branch or iteration scope the handler cannot see.
        if (failureVariables is not null)
            Emit(new VariablesMerged(handlerScopeId, failureVariables));

        // 4. Spawn the ErrorStartEvent inside the EventSubProcess scope.
        var errorStart = eventSubProcess.Activities.OfType<ErrorStartEvent>().First();
        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: errorStart.ActivityId,
            ActivityType: nameof(ErrorStartEvent),
            VariablesId: handlerScopeId,
            ScopeId: espInstanceId,
            MultiInstanceIndex: null,
            TokenId: null));

        // Deregister peer event-sub listeners on the enclosing scope — we
        // interrupted the scope, so any still-armed event-sub listeners must not fire.
        effects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
            enclosingScope, enclosingScopeContainerId,
            scopeVariablesId: enclosingScopeVariablesId,
            skipStartEventActivityId: null));

        return true;
    }

    /// <summary>
    /// Shared spawn path for all trigger types (timer/message/signal, interrupting
    /// and non-interrupting). Cancels siblings (interrupting only), spawns the
    /// EventSubProcess host + child variable scope, seeds the handler scope with
    /// either a clone of the enclosing scope (non-interrupting) or the delivered
    /// variables (interrupting message), spawns the start event inside the host,
    /// and either deregisters peer listeners (interrupting) or re-arms this listener
    /// (non-interrupting message/signal; timer cycle handled by caller).
    /// </summary>
    private void SpawnEventSubProcessHandler(
        EventSubProcess eventSubProcess,
        Activity startEvent,
        string startEventType,
        IWorkflowDefinition enclosingScope,
        Guid? enclosingScopeContainerId,
        Guid enclosingScopeVariablesId,
        ExpandoObject? deliveredVariables,
        List<IInfrastructureEffect> effects,
        string cancelReason)
    {
        if (eventSubProcess.IsInterrupting)
        {
            if (enclosingScopeContainerId.HasValue)
            {
                effects.AddRange(CancelScopeChildren(enclosingScopeContainerId.Value));
            }
            else
            {
                foreach (var sibling in _state.GetActiveActivities()
                    .Where(e => e.ScopeId is null)
                    .ToList())
                {
                    if (_state.HasActiveChildrenInScope(sibling.ActivityInstanceId))
                        effects.AddRange(CancelScopeChildren(sibling.ActivityInstanceId));

                    Emit(new ActivityCancelled(sibling.ActivityInstanceId, cancelReason));
                    effects.AddRange(BuildCancelledEntryCleanupEffects(sibling));
                }
            }
        }

        var espInstanceId = Guid.NewGuid();
        Emit(new ActivitySpawned(
            ActivityInstanceId: espInstanceId,
            ActivityId: eventSubProcess.ActivityId,
            ActivityType: nameof(EventSubProcess),
            VariablesId: enclosingScopeVariablesId,
            ScopeId: enclosingScopeContainerId,
            MultiInstanceIndex: null,
            TokenId: null));

        var handlerScopeId = Guid.NewGuid();
        Emit(new ChildVariableScopeCreated(handlerScopeId, enclosingScopeVariablesId));
        Emit(new ScopeHostExecutionScopeOpened(espInstanceId, handlerScopeId));

        // Non-interrupting: seed the isolated handler scope with a snapshot of the
        // enclosing scope's merged variables so the handler sees a consistent view
        // at the moment of firing but cannot mutate the parent.
        if (!eventSubProcess.IsInterrupting)
        {
            var clonedVars = _state.GetMergedVariables(enclosingScopeVariablesId);
            if (((IDictionary<string, object?>)clonedVars).Count > 0)
                Emit(new VariablesMerged(handlerScopeId, clonedVars));
        }

        // Merge delivered message variables (interrupting OR non-interrupting) on
        // top of whatever is already in the handler scope.
        if (deliveredVariables is not null
            && ((IDictionary<string, object?>)deliveredVariables).Count > 0)
            Emit(new VariablesMerged(handlerScopeId, deliveredVariables));

        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: startEvent.ActivityId,
            ActivityType: startEventType,
            VariablesId: handlerScopeId,
            ScopeId: espInstanceId,
            MultiInstanceIndex: null,
            TokenId: null));

        if (eventSubProcess.IsInterrupting)
        {
            effects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
                enclosingScope, enclosingScopeContainerId,
                scopeVariablesId: enclosingScopeVariablesId,
                skipStartEventActivityId: startEvent.ActivityId));
        }
    }

    /// <summary>
    /// Mirrors <see cref="TryActivateErrorEventSubProcess"/> for timer-triggered event
    /// sub-processes. Invoked from <see cref="HandleTimerFired"/> when the timer's
    /// activity id matches a <see cref="TimerStartEvent"/> inside an
    /// <see cref="EventSubProcess"/>. Handles both interrupting and non-interrupting
    /// variants, and re-registers cycle timers on each fire.
    /// </summary>
    private IReadOnlyList<IInfrastructureEffect> TryActivateTimerEventSubProcess(
        EventSubProcess eventSubProcess,
        IWorkflowDefinition enclosingScope,
        Guid scopeContainerHostId)
    {
        var timerStart = eventSubProcess.Activities.OfType<TimerStartEvent>().First();

        var enclosingScopeContainerId = scopeContainerHostId == _state.Id
            ? (Guid?)null
            : scopeContainerHostId;

        if (enclosingScopeContainerId.HasValue)
        {
            var containerEntry = _state.FindEntry(enclosingScopeContainerId.Value);
            if (containerEntry is null || containerEntry.IsCompleted)
                return [];
        }
        else if (_state.IsCompleted)
        {
            return [];
        }

        // Interrupting one-shot guard: if the handler is already running in this scope,
        // skip re-entry. Non-interrupting can fire concurrently so no guard there.
        if (eventSubProcess.IsInterrupting
            && _state.GetActiveActivities().Any(e =>
                e.ActivityId == eventSubProcess.ActivityId
                && e.ScopeId == enclosingScopeContainerId))
            return [];

        var enclosingScopeVariablesId = ResolveEnclosingScopeVariablesId(enclosingScopeContainerId);
        var effects = new List<IInfrastructureEffect>();

        SpawnEventSubProcessHandler(
            eventSubProcess, timerStart, nameof(TimerStartEvent),
            enclosingScope, enclosingScopeContainerId, enclosingScopeVariablesId,
            deliveredVariables: null, effects,
            cancelReason: $"Scope cancelled by timer event sub-process '{eventSubProcess.ActivityId}'");

        // Cycle timer re-registration: whether interrupting or non-interrupting, a
        // cycle-typed timer must re-arm for its next iteration. For one-shot timers,
        // the callback grain self-deactivates. For interrupting cycle, the peer
        // unregister path invoked by SpawnEventSubProcessHandler will tear the
        // subscription down — we only re-register for non-interrupting cycle.
        if (!eventSubProcess.IsInterrupting
            && timerStart.TimerDefinition.Type == TimerType.Cycle)
        {
            var nextCycle = timerStart.TimerDefinition.DecrementCycle();
            if (nextCycle is not null)
            {
                effects.Add(new RegisterTimerEffect(
                    _state.Id,
                    enclosingScopeContainerId ?? _state.Id,
                    timerStart.ActivityId,
                    nextCycle.GetDueTime()));
            }
        }

        return effects.AsReadOnly();
    }

    /// <summary>
    /// Mirrors <see cref="TryActivateTimerEventSubProcess"/> for message-triggered event
    /// sub-processes. Invoked from <see cref="HandleMessageDelivery"/> when the delivered
    /// message targets a <see cref="MessageStartEvent"/> inside an <see cref="EventSubProcess"/>.
    /// Interrupting-only (slice #D). Delivered variables are merged into the handler's
    /// isolated child variable scope so the handler script can access them.
    /// </summary>
    private IReadOnlyList<IInfrastructureEffect> TryActivateMessageEventSubProcess(
        EventSubProcess eventSubProcess,
        IWorkflowDefinition enclosingScope,
        Guid scopeContainerHostId,
        ExpandoObject deliveredVariables)
    {
        var messageStart = eventSubProcess.Activities.OfType<MessageStartEvent>().First();

        var enclosingScopeContainerId = scopeContainerHostId == _state.Id
            ? (Guid?)null
            : scopeContainerHostId;

        if (enclosingScopeContainerId.HasValue)
        {
            var containerEntry = _state.FindEntry(enclosingScopeContainerId.Value);
            if (containerEntry is null || containerEntry.IsCompleted)
                return [];
        }
        else if (_state.IsCompleted)
        {
            return [];
        }

        if (eventSubProcess.IsInterrupting
            && _state.GetActiveActivities().Any(e =>
                e.ActivityId == eventSubProcess.ActivityId
                && e.ScopeId == enclosingScopeContainerId))
            return [];

        var enclosingScopeVariablesId = ResolveEnclosingScopeVariablesId(enclosingScopeContainerId);
        var effects = new List<IInfrastructureEffect>();

        SpawnEventSubProcessHandler(
            eventSubProcess, messageStart, nameof(MessageStartEvent),
            enclosingScope, enclosingScopeContainerId, enclosingScopeVariablesId,
            deliveredVariables, effects,
            cancelReason: $"Scope cancelled by message event sub-process '{eventSubProcess.ActivityId}'");

        // Non-interrupting: DeliverMessage consumed the previous subscription, so
        // re-subscribe to keep the listener armed for subsequent messages. The
        // correlation grain is [Reentrant] so this call from within the active
        // DeliverMessage does not deadlock.
        if (!eventSubProcess.IsInterrupting)
        {
            var messageDef = _definition.GetMessageDefinition(messageStart.MessageDefinitionId);
            var correlationKey = ResolveCorrelationKey(messageDef, enclosingScopeVariablesId);
            effects.Add(new SubscribeMessageEffect(
                messageDef.Name, correlationKey,
                _state.Id, messageStart.ActivityId,
                enclosingScopeContainerId ?? _state.Id));
        }

        return effects.AsReadOnly();
    }

    /// <summary>
    /// Mirrors <see cref="TryActivateMessageEventSubProcess"/> for signal-triggered
    /// event sub-processes. Signal delivery carries no payload so the handler's
    /// child scope is empty (no merge). Interrupting-only (slice #E).
    /// </summary>
    private IReadOnlyList<IInfrastructureEffect> TryActivateSignalEventSubProcess(
        EventSubProcess eventSubProcess,
        IWorkflowDefinition enclosingScope,
        Guid scopeContainerHostId)
    {
        var signalStart = eventSubProcess.Activities.OfType<SignalStartEvent>().First();

        var enclosingScopeContainerId = scopeContainerHostId == _state.Id
            ? (Guid?)null
            : scopeContainerHostId;

        if (enclosingScopeContainerId.HasValue)
        {
            var containerEntry = _state.FindEntry(enclosingScopeContainerId.Value);
            if (containerEntry is null || containerEntry.IsCompleted)
                return [];
        }
        else if (_state.IsCompleted)
        {
            return [];
        }

        if (eventSubProcess.IsInterrupting
            && _state.GetActiveActivities().Any(e =>
                e.ActivityId == eventSubProcess.ActivityId
                && e.ScopeId == enclosingScopeContainerId))
            return [];

        var enclosingScopeVariablesId = ResolveEnclosingScopeVariablesId(enclosingScopeContainerId);
        var effects = new List<IInfrastructureEffect>();

        SpawnEventSubProcessHandler(
            eventSubProcess, signalStart, nameof(SignalStartEvent),
            enclosingScope, enclosingScopeContainerId, enclosingScopeVariablesId,
            deliveredVariables: null, effects,
            cancelReason: $"Scope cancelled by signal event sub-process '{eventSubProcess.ActivityId}'");

        // Non-interrupting: re-subscribe to keep the listener armed for subsequent
        // broadcasts. SignalCorrelationGrain is [Reentrant] so the re-subscribe
        // from within the active BroadcastSignal call does not deadlock.
        if (!eventSubProcess.IsInterrupting)
        {
            var signalDef = _definition.GetSignalDefinition(signalStart.SignalDefinitionId);
            effects.Add(new SubscribeSignalEffect(
                signalDef.Name, _state.Id, signalStart.ActivityId,
                enclosingScopeContainerId ?? _state.Id));
        }

        return effects.AsReadOnly();
    }

    /// <summary>
    /// Produces unregister/unsubscribe effects for every event sub-process listener
    /// (timer and message) declared directly inside <paramref name="scope"/>, optionally
    /// skipping a single start-event activity id (used when an event sub-process fires
    /// and its own listener has already been consumed on the infrastructure side).
    ///
    /// The message correlation key is re-resolved against <paramref name="scopeVariablesId"/>
    /// — the same value used at registration — so unsubscribe targets the same
    /// subscription entry. Callers pass the enclosing scope's variables id (e.g. the
    /// SubProcess host's variables or the root variables).
    /// </summary>
    private List<IInfrastructureEffect> BuildEventSubProcessPeerUnregisterEffects(
        IWorkflowDefinition scope, Guid? scopeContainerId, Guid scopeVariablesId,
        string? skipStartEventActivityId)
    {
        var hostId = scopeContainerId ?? _state.Id;
        var list = new List<IInfrastructureEffect>();
        foreach (var (_, timerStart) in scope.GetEventSubProcessTimers())
        {
            if (timerStart.ActivityId == skipStartEventActivityId) continue;
            list.Add(new UnregisterTimerEffect(_state.Id, hostId, timerStart.ActivityId));
        }
        foreach (var (_, messageStart) in scope.GetEventSubProcessMessages())
        {
            if (messageStart.ActivityId == skipStartEventActivityId) continue;
            var messageDef = _definition.GetMessageDefinition(messageStart.MessageDefinitionId);
            var correlationKey = ResolveCorrelationKey(messageDef, scopeVariablesId);
            list.Add(new UnsubscribeMessageEffect(messageDef.Name, correlationKey));
        }
        foreach (var (_, signalStart) in scope.GetEventSubProcessSignals())
        {
            if (signalStart.ActivityId == skipStartEventActivityId) continue;
            var signalDef = _definition.GetSignalDefinition(signalStart.SignalDefinitionId);
            list.Add(new UnsubscribeSignalEffect(signalDef.Name, _state.Id, signalStart.ActivityId, hostId));
        }
        return list;
    }

    private Guid? FindEnclosingScopeContainerId(
        ActivityInstanceEntry failedEntry, IWorkflowDefinition enclosingScope)
    {
        if (enclosingScope is WorkflowDefinition) return null;

        var targetScopeActivityId = enclosingScope switch
        {
            SubProcess sp => sp.ActivityId,
            EventSubProcess esp => esp.ActivityId,
            _ => throw new InvalidOperationException(
                $"Unexpected enclosing scope type {enclosingScope.GetType().Name}"),
        };

        var current = failedEntry;
        while (current.ScopeId.HasValue)
        {
            var parent = _state.GetEntry(current.ScopeId.Value);
            if (parent.ActivityId == targetScopeActivityId)
                return parent.ActivityInstanceId;
            current = parent;
        }
        throw new InvalidOperationException(
            $"Could not locate enclosing scope entry for '{targetScopeActivityId}' from failing entry '{failedEntry.ActivityId}'");
    }

    /// <summary>
    /// The variable scope an event sub-process in this container reads and is seeded from: the
    /// container's own execution scope, not the scope the container host runs in (#840).
    /// </summary>
    private Guid ResolveEnclosingScopeVariablesId(Guid? enclosingScopeContainerId)
    {
        if (enclosingScopeContainerId.HasValue)
            return _state.ScopeHostExecutionScopes.TryGetValue(enclosingScopeContainerId.Value, out var ownScopeId)
                ? ownScopeId
                : _state.GetEntry(enclosingScopeContainerId.Value).VariablesId;

        return _state.GetRootVariablesId();
    }

    // --- Activity Lifecycle Helpers ---

    /// <summary>The entry a callback addresses; null when an instance id's entry was finished and pruned (#1050).</summary>
    private ActivityInstanceEntry? ResolveEntry(string activityId, Guid? activityInstanceId)
    {
        if (activityInstanceId.HasValue)
        {
            return _state.FindEntry(activityInstanceId.Value);
        }

        return _state.GetFirstActive(activityId)
            ?? throw new InvalidOperationException(
                $"No active entry found for activity '{activityId}'.");
    }

    // --- Boundary Event Handling ---

    /// <summary>
    /// Unified handler for boundary timer, message, and signal events firing. Interrupting:
    /// cancels the host and releases everything it held. Non-interrupting: clones the host's
    /// variables and re-arms a cycle timer. Either way it spawns the boundary event.
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

        if (isInterrupting)
        {
            Emit(new ActivityCancelled(
                hostEntry.ActivityInstanceId,
                $"Interrupted by boundary event '{boundaryActivity.ActivityId}'"));
            variablesId = hostEntry.VariablesId;
        }
        else
        {
            // Non-interrupting: the host keeps running; the boundary path gets a detached copy.
            variablesId = Guid.NewGuid();
            Emit(new VariableScopeCloned(variablesId, hostEntry.VariablesId, Detached: true));

            // A cycle timer re-arms with one repetition fewer (tracked state, else the definition).
            if (boundaryActivity is BoundaryTimerEvent boundaryTimer
                && boundaryTimer.TimerDefinition.Type == TimerType.Cycle)
            {
                var currentCycle = _state.GetTimerCycleState(
                    hostEntry.ActivityInstanceId, boundaryTimer.ActivityId)
                    ?? boundaryTimer.TimerDefinition;
                var nextCycle = currentCycle.DecrementCycle();
                Emit(new TimerCycleUpdated(hostEntry.ActivityInstanceId, boundaryTimer.ActivityId, nextCycle));
                if (nextCycle is not null)
                    effects.Add(new RegisterTimerEffect(
                        _state.Id, hostEntry.ActivityInstanceId, boundaryTimer.ActivityId, nextCycle.GetDueTime()));
            }
        }

        var boundaryInstanceId = Guid.NewGuid();
        Emit(new ActivitySpawned(
            ActivityInstanceId: boundaryInstanceId,
            ActivityId: boundaryActivity.ActivityId,
            ActivityType: boundaryActivity.GetType().Name,
            VariablesId: variablesId,
            ScopeId: hostEntry.ScopeId,
            MultiInstanceIndex: null,
            TokenId: null));

        // The delivered payload (e.g. a message's variables) is the boundary event's result: it
        // sits in the event's local scope until completion, where its output mappings filter it
        // (or, without outputs, all of it merges into the scope above).
        if (((IDictionary<string, object?>)deliveredVariables).Count > 0)
            Emit(new ActivityInputScopeOpened(boundaryInstanceId, Guid.NewGuid(), deliveredVariables));

        if (isInterrupting)
        {
            effects.AddRange(BuildUserTaskCleanupEffects(hostEntry.ActivityInstanceId));
            effects.AddRange(BuildChildWorkflowCancelEffects(hostEntry));
            effects.AddRange(CancelScopeChildren(hostEntry.ActivityInstanceId));

            // Clean up child variable scope of the interrupted sub-process
            // Filter to scopes that have a parent (i.e. true child scopes), rather than
            // "not equal to host scope" — this is safer against entries that may reference
            // an unrelated scope. Matches the convention used by sub-process completion cleanup.
            var interruptedChildScopes = _state.GetEntriesInScope(hostEntry.ActivityInstanceId)
                .Select(e => e.VariablesId)
                .Where(vid => _state.VariableStates.Any(vs => vs.Id == vid && vs.ParentVariablesId.HasValue))
                .Distinct()
                .ToList();
            if (interruptedChildScopes.Count > 0)
            {
                Emit(new VariableScopesRemoved(interruptedChildScopes));
            }

            effects.AddRange(BuildBoundaryUnsubscribeEffects(
                attachedToActivityId, hostEntry,
                skipTimerActivityId, skipMessageName, skipSignalName));

            // An interrupted intermediate catch host must also drop its OWN trigger
            // subscription. Otherwise e.g. a message catch's correlation grain stays
            // subscribed forever and the next instance with the same correlation key fails
            // with "Duplicate subscription" (#759).
            var hostActivity = _definition.GetActivityAcrossScopes(attachedToActivityId);
            effects.AddRange(BuildCatchEventTriggerCleanupEffects(hostActivity, hostEntry));
        }

        return effects.AsReadOnly();
    }

    /// <summary>
    /// Builds the cleanup effects for an intermediate catch event's own trigger
    /// (timer reminder, message subscription, signal subscription) when its entry is
    /// cancelled before the trigger fired. Returns no effects for any other activity type.
    /// </summary>
    private List<IInfrastructureEffect> BuildCatchEventTriggerCleanupEffects(
        Activity catchActivity, ActivityInstanceEntry catchEntry)
    {
        var effects = new List<IInfrastructureEffect>();
        switch (catchActivity)
        {
            case TimerIntermediateCatchEvent:
                effects.Add(new UnregisterTimerEffect(
                    _state.Id, catchEntry.ActivityInstanceId, catchActivity.ActivityId));
                break;

            case MessageIntermediateCatchEvent msgCatch:
                var messageDef = _definition.GetMessageDefinition(msgCatch.MessageDefinitionId);
                var correlationKey = ResolveCorrelationKey(messageDef, catchEntry.VariablesId);
                effects.Add(new UnsubscribeMessageEffect(messageDef.Name, correlationKey));
                break;

            case SignalIntermediateCatchEvent sigCatch:
                var signalDef = _definition.GetSignalDefinition(sigCatch.SignalDefinitionId);
                effects.Add(new UnsubscribeSignalEffect(signalDef.Name, _state.Id, catchActivity.ActivityId, catchEntry.ActivityInstanceId));
                break;

            case ConditionalIntermediateCatchEvent:
                if (_state.ConditionalWatchers.Any(w => w.ActivityInstanceId == catchEntry.ActivityInstanceId))
                    Emit(new ConditionalWatcherCleared(catchEntry.ActivityInstanceId));
                break;

            case MultipleIntermediateCatchEvent multiCatch:
                effects.AddRange(CancelMultipleEventSiblings(
                    multiCatch, catchEntry,
                    skipMessageName: null, skipSignalName: null, skipTimerActivityId: null));
                break;
        }
        return effects;
    }

    private Guid? FindDescendantHazardTx(Guid outerTxScopeId)
    {
        foreach (var (txId, outcome) in _state.TransactionOutcomes)
        {
            if (outcome.Outcome != TransactionOutcome.Hazard) continue;
            if (IsDescendantOfScope(txId, outerTxScopeId))
                return txId;
        }
        return null;
    }

    private IEnumerable<Guid> DescendantScopesWithActiveWalk(Guid rootScopeId)
    {
        foreach (var scopeKey in _state.ActiveCompensationWalks.Keys)
        {
            if (scopeKey == Guid.Empty) continue;
            if (IsDescendantOfScope(scopeKey, rootScopeId))
                yield return scopeKey;
        }
    }

    private bool IsDescendantOfScope(Guid childScopeId, Guid ancestorScopeId)
    {
        var entry = _state.FindEntry(childScopeId);
        if (entry is null) return false;
        if (entry.ScopeId == ancestorScopeId) return true;
        if (!entry.ScopeId.HasValue) return false;
        return IsDescendantOfScope(entry.ScopeId.Value, ancestorScopeId);
    }

    private void AbortCompensationWalk(Guid? scopeId, string reason)
    {
        var walk = _state.GetCompensationWalk(scopeId);
        if (walk is null) return;

        if (walk.CurrentHandlerInstanceId.HasValue)
        {
            var handlerEntry = _state.FindEntry(walk.CurrentHandlerInstanceId.Value);
            if (handlerEntry is not null && !handlerEntry.IsCompleted && !handlerEntry.IsCancelled)
                Emit(new ActivityCancelled(walk.CurrentHandlerInstanceId.Value, "CompensationHandlerAborted"));
        }

        Emit(new CompensationWalkAborted(scopeId, reason));
    }

    /// <summary>
    /// Recursively cancels all active entries within a scope (and their nested scope children).
    /// </summary>
    private List<IInfrastructureEffect> CancelScopeChildren(
        Guid scopeId, string cancellationReason = "Scope cancelled by boundary event")
    {
        var effects = new List<IInfrastructureEffect>();
        foreach (var entry in _state.GetActiveActivities()
            .Where(e => e.ScopeId == scopeId).ToList())
        {
            // Recursively cancel nested scope children first
            if (_state.HasActiveChildrenInScope(entry.ActivityInstanceId))
                effects.AddRange(CancelScopeChildren(entry.ActivityInstanceId, cancellationReason));

            Emit(new ActivityCancelled(
                entry.ActivityInstanceId,
                cancellationReason));

            effects.AddRange(BuildCancelledEntryCleanupEffects(entry));
        }

        // Unregister event sub-process listeners declared inside the cancelled scope
        // (e.g. a SubProcess being interrupted by a boundary event on its host).
        var scopeEntry = _state.FindEntry(scopeId);
        if (scopeEntry is not null)
        {
            var scopeActivity = _definition.GetActivityAcrossScopes(scopeEntry.ActivityId);
            if (scopeActivity is SubProcess cancelledSub)
            {
                // Keys resolve against the sub-process's own scope, where they were registered.
                effects.AddRange(BuildEventSubProcessPeerUnregisterEffects(
                    cancelledSub, scopeId,
                    scopeVariablesId: ResolveEnclosingScopeVariablesId(scopeId),
                    skipStartEventActivityId: null));
            }
        }

        return effects;
    }

    // --- Boundary Unsubscribe Helpers ---

    /// <summary>
    /// Everything a cancelled entry still holds outside the aggregate, released in one place
    /// (#847): its user-task record, its own catch trigger (timer, message, signal, conditional),
    /// its boundary subscriptions and watchers, and — for a call activity — its child instance.
    /// Call it right after the entry's <see cref="ActivityCancelled"/>.
    /// </summary>
    private List<IInfrastructureEffect> BuildCancelledEntryCleanupEffects(ActivityInstanceEntry entry)
    {
        var effects = BuildUserTaskCleanupEffects(entry.ActivityInstanceId);
        var activity = _definition.FindScopeForActivity(entry.ActivityId)?.GetActivity(entry.ActivityId);
        if (activity is null) return effects;

        effects.AddRange(BuildCatchEventTriggerCleanupEffects(activity, entry));

        // Boundaries belong to a multi-instance host, not to its iterations.
        if (entry.MultiInstanceIndex is null)
            effects.AddRange(BuildBoundaryUnsubscribeEffects(entry.ActivityId, entry));

        effects.AddRange(BuildChildWorkflowCancelEffects(entry));
        return effects;
    }

    /// <summary>A cancelled call activity takes its child instance down with it (#847).</summary>
    private static List<IInfrastructureEffect> BuildChildWorkflowCancelEffects(ActivityInstanceEntry entry)
        => entry.ChildWorkflowInstanceId is Guid childId
            ? [new CancelChildWorkflowEffect(childId,
                $"Call activity '{entry.ActivityId}' was cancelled in the parent instance")]
            : [];

    /// <summary>
    /// Builds unsubscribe effects for boundary subscriptions, with optional skip parameters
    /// to avoid unsubscribing the boundary that just fired (which could cause deadlocks).
    /// </summary>
    private List<IInfrastructureEffect> BuildBoundaryUnsubscribeEffects(
        string activityId, ActivityInstanceEntry hostEntry,
        string? skipTimerActivityId = null, string? skipMessageName = null, string? skipSignalName = null)
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
            effects.Add(new UnsubscribeSignalEffect(signalDef.Name, _state.Id, boundarySig.ActivityId, hostEntry.ActivityInstanceId));
        }

        // Conditional boundary watchers are keyed by the host instance; nothing external to release.
        foreach (var watcher in _state.ConditionalWatchers
            .Where(w => w.ActivityInstanceId == hostEntry.ActivityInstanceId
                && scope.Activities.OfType<ConditionalBoundaryEvent>()
                    .Any(cb => cb.ActivityId == w.ActivityId && cb.AttachedToActivityId == activityId))
            .ToList())
        {
            Emit(new ConditionalWatcherCleared(watcher.ActivityInstanceId));
        }

        // Boundary multiple events (skip definitions that just fired)
        foreach (var boundaryMulti in scope.GetBoundaryMultipleEvents(activityId))
        {
            effects.AddRange(BuildMultipleBoundaryUnsubscribeEffects(
                boundaryMulti, hostEntry,
                skipActivityId: skipTimerActivityId,
                skipMessageName: skipMessageName,
                skipSignalName: skipSignalName));
        }

        return effects;
    }

    private List<IInfrastructureEffect> CancelEventBasedGatewaySiblings(
        ActivityInstanceEntry completedEntry)
    {
        var effects = new List<IInfrastructureEffect>();
        var siblings = _definition.GetEventBasedGatewaySiblings(completedEntry.ActivityId);

        if (siblings.Count == 0) return effects;

        foreach (var siblingId in siblings)
        {
            // Only the sibling of this gateway pass: the same scope instance, not another
            // multi-instance iteration's or another scope's copy of the gateway (#844).
            var siblingEntry = _state.GetActiveActivities()
                .FirstOrDefault(e => e.ActivityId == siblingId && e.ScopeId == completedEntry.ScopeId);
            if (siblingEntry is null) continue;

            Emit(new ActivityCancelled(
                siblingEntry.ActivityInstanceId,
                "Event-based gateway: sibling event completed"));

            // Build unsubscribe effects based on sibling type
            var siblingActivity = _definition.GetActivityAcrossScopes(siblingId);
            effects.AddRange(BuildCatchEventTriggerCleanupEffects(siblingActivity, siblingEntry));
        }

        return effects;
    }

    /// <summary>
    /// Cancels sibling watchers for a Multiple Intermediate Catch Event when one definition fires.
    /// Modeled on CancelEventBasedGatewaySiblings but iterates the definitions list
    /// instead of looking up sibling activity entries.
    /// </summary>
    private List<IInfrastructureEffect> CancelMultipleEventSiblings(
        MultipleIntermediateCatchEvent multipleEvent,
        ActivityInstanceEntry completedEntry,
        string? skipMessageName,
        string? skipSignalName,
        string? skipTimerActivityId)
    {
        var effects = new List<IInfrastructureEffect>();

        foreach (var definition in multipleEvent.Definitions)
        {
            switch (definition)
            {
                case MessageEventDef msgDef:
                    var messageDef = _definition.GetMessageDefinition(msgDef.MessageDefinitionId);
                    if (messageDef.Name == skipMessageName) continue;
                    var correlationKey = ResolveCorrelationKey(messageDef, completedEntry.VariablesId);
                    effects.Add(new UnsubscribeMessageEffect(messageDef.Name, correlationKey));
                    break;

                case SignalEventDef sigDef:
                    var signalDef = _definition.GetSignalDefinition(sigDef.SignalDefinitionId);
                    if (signalDef.Name == skipSignalName) continue;
                    effects.Add(new UnsubscribeSignalEffect(
                        signalDef.Name, _state.Id, multipleEvent.ActivityId, completedEntry.ActivityInstanceId));
                    break;

                case TimerEventDef:
                    if (multipleEvent.ActivityId == skipTimerActivityId) continue;
                    effects.Add(new UnregisterTimerEffect(
                        _state.Id, completedEntry.ActivityInstanceId,
                        multipleEvent.ActivityId));
                    break;
            }
        }

        return effects;
    }

    /// <summary>
    /// Builds unsubscribe effects for all definitions in a MultipleBoundaryEvent,
    /// with skip parameters to avoid unsubscribing the definition that just fired.
    /// </summary>
    private List<IInfrastructureEffect> BuildMultipleBoundaryUnsubscribeEffects(
        MultipleBoundaryEvent boundaryMulti,
        ActivityInstanceEntry hostEntry,
        string? skipActivityId,
        string? skipMessageName,
        string? skipSignalName)
    {
        var effects = new List<IInfrastructureEffect>();

        foreach (var definition in boundaryMulti.Definitions)
        {
            switch (definition)
            {
                case TimerEventDef:
                    if (boundaryMulti.ActivityId == skipActivityId) continue;
                    effects.Add(new UnregisterTimerEffect(
                        _state.Id, hostEntry.ActivityInstanceId, boundaryMulti.ActivityId));
                    break;

                case MessageEventDef msgDef:
                    var messageDef = _definition.GetMessageDefinition(msgDef.MessageDefinitionId);
                    if (messageDef.Name == skipMessageName) continue;
                    var correlationKey = ResolveCorrelationKey(messageDef, hostEntry.VariablesId);
                    effects.Add(new UnsubscribeMessageEffect(messageDef.Name, correlationKey));
                    break;

                case SignalEventDef sigDef:
                    var signalDef = _definition.GetSignalDefinition(sigDef.SignalDefinitionId);
                    if (signalDef.Name == skipSignalName) continue;
                    effects.Add(new UnsubscribeSignalEffect(
                        signalDef.Name, _state.Id, boundaryMulti.ActivityId, hostEntry.ActivityInstanceId));
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

    // --- Pending external events durability (#657) ---

    /// <summary>Emits <see cref="PendingEventDrained"/>, removing a processed external event from the queue ledger.</summary>
    public void EmitPendingEventDrained(string opKey)
        => Emit(new PendingEventDrained(opKey));

    /// <summary>Emits <see cref="PendingEventApplied"/>, recording a retried op's outcome in the dedup ledger.</summary>
    public void EmitPendingEventApplied(string opKey, PendingEventResult? result)
        => Emit(new PendingEventApplied(opKey, result));

    // --- Emit / Apply pattern ---

    private void Emit(WorkflowEvent @event)
    {
        // Copy mutable ExpandoObject data to preserve event immutability, keeping only the
        // variables the merge changes so the journal stores a delta, not the whole scope (#867).
        var snapshotted = @event switch
        {
            ActivityCompleted e => e with { Variables = ChangedVariables(e.VariablesId, e.Variables) },
            VariablesMerged e => e with { Variables = ChangedVariables(e.VariablesId, e.Variables) },
            _ => @event
        };

        var stamped = Stamp(snapshotted);
        Apply(stamped);
        _uncommittedEvents.Add(stamped);

        if (stamped is ActivityCompleted completed)
        {
            _transitionsPending = true;
            if (((IDictionary<string, object?>)completed.Variables).Count > 0)
                _recentWrites.Add((completed.VariablesId, completed.Variables));
        }
    }

    // An activity completed whose outgoing transitions the execution loop has not taken yet: an
    // end event must not complete the instance now, as a successor is about to spawn. Not state.
    private bool _transitionsPending;

    /// <summary>
    /// The execution loop took the transitions of every completion so far. Until it says so, an end
    /// event defers the instance's completion to <see cref="TryDeferredWorkflowCompletion"/>.
    /// </summary>
    public void MarkTransitionsTaken() => _transitionsPending = false;

    // What completions wrote, and into which scope, since the last ShareRecentWritesWithWatchers;
    // not state, never journaled.
    private List<(Guid VariablesId, ExpandoObject Variables)> _recentWrites = [];

    /// <summary>True when a completion wrote variables that conditional watchers have not seen yet.</summary>
    public bool HasRecentWrites => _recentWrites.Count > 0;

    /// <summary>
    /// Process data is shared (BPMN), but a watcher reads its own scope, which a write on another
    /// parallel branch never reaches (#784). Merges each recent write into the scope of every
    /// conditional watcher that can't see it, as journaled events, so the watcher's next evaluation
    /// and its false-to-true edge detection see a stable value. A branch's scope — a parallel
    /// branch's clone, a non-interrupting boundary's detached clone — is shared. A write into an
    /// isolated scope — a non-interrupting event sub-process, a multi-instance iteration — stays
    /// there: it is never merged into the rest of the instance either.
    /// </summary>
    public void ShareRecentWritesWithWatchers()
    {
        var writes = _recentWrites;
        _recentWrites = [];
        if (_state.ConditionalWatchers.Count == 0) return;

        var isolated = IsolatedScopeIds();
        var shared = writes.Where(w => !IsInIsolatedScope(w.VariablesId, isolated)).ToList();
        if (shared.Count == 0) return;

        foreach (var watcher in _state.ConditionalWatchers.ToList())
        {
            var visible = ScopeChain(watcher.VariablesId);
            var current = (IDictionary<string, object?>)_state.GetMergedVariables(watcher.VariablesId);
            var delta = new ExpandoObject();
            var deltaDict = (IDictionary<string, object?>)delta;
            foreach (var (scopeId, variables) in shared)
            {
                if (visible.Contains(scopeId)) continue;
                foreach (var (key, value) in (IDictionary<string, object?>)variables)
                    if (!current.TryGetValue(key, out var existing) || !Equals(existing, value))
                        deltaDict[key] = value;
            }
            if (deltaDict.Count > 0)
                Emit(new VariablesMerged(watcher.VariablesId, delta));
        }
    }

    private HashSet<Guid> IsolatedScopeIds()
    {
        var isolated = new HashSet<Guid>();
        foreach (var entry in _state.Entries)
        {
            if (entry.MultiInstanceIndex is not null)
                isolated.Add(entry.VariablesId);
            else if (_state.ScopeHostExecutionScopes.TryGetValue(entry.ActivityInstanceId, out var ownScope)
                && _definition.GetActivityAcrossScopes(entry.ActivityId) is EventSubProcess { IsInterrupting: false })
                isolated.Add(ownScope);
        }
        return isolated;
    }

    private bool IsInIsolatedScope(Guid variablesId, HashSet<Guid> isolated)
        => ScopeChain(variablesId).Overlaps(isolated);

    private HashSet<Guid> ScopeChain(Guid variablesId)
    {
        var chain = new HashSet<Guid>();
        for (Guid? id = variablesId; id is { } current && chain.Add(current);)
            id = _state.VariableStates.FirstOrDefault(v => v.Id == current)?.ParentVariablesId;
        return chain;
    }

    /// <summary>
    /// The only clock read on the event path: sets <see cref="WorkflowEvent.OccurredAt"/>. Apply
    /// handlers take the time from the event, so replaying the journal reproduces it. Used by
    /// <see cref="Emit"/> and by the grain's durable enqueue, which raises its event directly.
    /// </summary>
    public static WorkflowEvent Stamp(WorkflowEvent @event) => @event with { OccurredAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// The variables merging <paramref name="variables"/> into scope <paramref name="variablesId"/>
    /// would change. Apply merges key by key, so leaving out values the scope already holds yields
    /// the same state, live and on replay. An unknown scope keeps everything — Apply then throws
    /// exactly as before.
    /// </summary>
    private ExpandoObject ChangedVariables(Guid variablesId, ExpandoObject variables)
    {
        var scope = _state.VariableStates.FirstOrDefault(v => v.Id == variablesId);
        return scope is null ? SnapshotExpandoObject(variables) : scope.ChangesFrom(variables);
    }

    private static ExpandoObject SnapshotExpandoObject(ExpandoObject source)
    {
        var snapshot = new ExpandoObject();
        var target = (IDictionary<string, object?>)snapshot;
        foreach (var kvp in (IDictionary<string, object?>)source)
            target[kvp.Key] = kvp.Value;
        return snapshot;
    }

    private void Apply(WorkflowEvent @event)
    {
        switch (@event)
        {
            case WorkflowStarted e:
                ApplyWorkflowStarted(e);
                break;
            case ExecutionStarted e:
                _state.Start(e.OccurredAt);
                break;
            case WorkflowCompleted e:
                _state.Complete(e.OccurredAt);
                break;
            case WorkflowCancelled e:
                _state.Cancel(e.OccurredAt);
                break;
            case WorkflowFailed e:
                _state.Fail(e.OccurredAt);
                break;
            case EscalationUncaughtRaised:
                break; // non-faulting per BPMN spec — recorded for observability only
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
                _state.ResetEntryExecuting(e.ActivityInstanceId);
                break;
            case ActivityCancelled e:
                ApplyActivityCancelled(e);
                break;
            case MultiInstanceTotalSet e:
                _state.SetEntryMultiInstanceTotal(e.ActivityInstanceId, e.Total);
                break;
            case VariablesMerged e:
                _state.MergeState(e.VariablesId, e.Variables);
                break;

            case ChildVariableScopeCreated e:
                _state.AddChildVariableState(e.ScopeId, e.ParentScopeId);
                break;
            case ScopeHostExecutionScopeOpened e:
                _state.ScopeHostExecutionScopes[e.HostInstanceId] = e.ExecutionScopeId;
                break;
            case VariableScopeCloned e:
                _state.AddCloneOfVariableState(e.NewScopeId, e.SourceScopeId, e.Detached);
                break;
            case VariableScopeTrimmed e:
                _state.TrimDetachedScope(e.ScopeId, e.RemovedNames);
                break;
            case VariableScopesRemoved e:
                _state.RemoveVariableStates(e.ScopeIds);
                break;
            case ActivityInputScopeOpened e:
                ApplyActivityInputScopeOpened(e);
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
            case ComplexGatewayJoinStateCreated e:
                _state.CreateComplexGatewayJoinState(e.GatewayActivityId, e.ScopeId, e.FirstActivityInstanceId, e.ActivationCondition, e.WorkflowInstanceId);
                break;
            case ComplexGatewayJoinStateTokenIncremented e:
                _state.IncrementComplexGatewayTokenCount(e.GatewayActivityId, e.ScopeId);
                break;
            case ComplexGatewayJoinStateFired e:
                _state.MarkComplexGatewayJoinFired(e.GatewayActivityId, e.ScopeId, e.ExpectedTokenCount);
                break;
            case ComplexGatewayJoinStateRemoved e:
                _state.RemoveComplexGatewayJoinState(e.GatewayActivityId, e.ScopeId);
                break;
            case ParentInfoSet e:
                _state.SetParentInfo(e.ParentInstanceId, e.ParentActivityId);
                break;
            case ChildWorkflowLinked e:
                _state.SetEntryChildWorkflowInstanceId(e.ActivityInstanceId, e.ChildWorkflowInstanceId);
                break;
            case UserTaskRegistered e:
                var meta = new UserTaskMetadata();
                meta.Initialize(e.Assignee, e.CandidateGroups, e.CandidateUsers, e.ExpectedOutputVariables);
                _state.UserTasks[e.ActivityInstanceId] = meta;
                break;
            case UserTaskClaimed e:
                _state.UserTasks[e.ActivityInstanceId].Claim(e.UserId, e.OccurredAt);
                break;
            case UserTaskUnclaimed e:
                _state.UserTasks[e.ActivityInstanceId].Unclaim();
                break;
            case UserTaskUnregistered e:
                _state.UserTasks.Remove(e.ActivityInstanceId);
                break;
            case UserTaskFailed e:
                if (_state.UserTasks.TryGetValue(e.ActivityInstanceId, out var failedTask))
                    failedTask.Fail();
                break;
            case UserTaskCancelled e:
                if (_state.UserTasks.TryGetValue(e.ActivityInstanceId, out var cancelledTask))
                    cancelledTask.Cancel();
                break;
            case CustomTaskClaimed e:
                if (_state.FindEntry(e.ActivityInstanceId) is { IsCompleted: false } claimed
                    && !claimed.IsCustomTaskClaimedByOther(e.DispatchId, e.OccurredAt))
                    claimed.ClaimCustomTask(e.DispatchId, e.LeaseUntil);
                break;
            case CustomTaskClaimReleased e:
                if (_state.FindEntry(e.ActivityInstanceId) is { } released && released.CustomTaskClaimId == e.DispatchId)
                    released.ReleaseCustomTaskClaim();
                break;
            case TimerCycleUpdated e:
                _state.SetTimerCycleState(e.HostActivityInstanceId, e.TimerActivityId, e.RemainingCycle);
                break;
            case ConditionalWatcherRegistered e:
                _state.AddConditionalWatcher(e.ActivityInstanceId, e.ActivityId, e.ConditionExpression, e.VariablesId);
                break;
            case ConditionalWatcherFired e:
                _state.RemoveConditionalWatcher(e.ActivityInstanceId);
                break;
            case ConditionalWatcherCleared e:
                _state.RemoveConditionalWatcher(e.ActivityInstanceId);
                break;
            case ConditionalWatcherResultUpdated e:
                _state.UpdateConditionalWatcherResult(e.ActivityInstanceId, e.Result);
                break;
            case CompensableActivitySnapshotRecorded e:
                _state.AddCompensationSnapshot(new CompletedActivitySnapshot(
                    e.ActivityInstanceId, e.ActivityDefinitionId,
                    e.VariablesSnapshot, _state.NextCompensationSequence, e.ScopeId));
                break;
            case CompensationWalkStarted e:
                if (e.HandlerCount > 0)
                    _state.StartCompensationWalk(e.ScopeId, new CompensationWalkState(e.ScopeId, e.TargetActivityRef, e.ThrowerActivityInstanceId));
                break;
            case CompensationHandlerSpawned e:
                _state.SetCompensationHandlerInstanceId(e.ScopeId, e.HandlerInstanceId, e.CompensatedActivityInstanceId);
                _state.MarkCurrentHandlerEntry(e.HandlerInstanceId);
                break;
            case CompensationEntryMarkedCompensated e:
                _state.MarkSnapshotCompensated(e.CompensatedActivityInstanceId);
                break;
            case CompensationWalkCompleted e:
                _state.ClearCompensationWalk(e.ScopeId);
                break;
            case CompensationWalkFailed:
                // Walk state intentionally NOT cleared — preserved for observability
                break;
            case CompensationWalkAborted e:
                _state.ClearCompensationWalk(e.ScopeId);
                break;
            case TransactionOutcomeSet e:
                _state.TransactionOutcomes[e.TransactionInstanceId] =
                    new TransactionOutcomeRecord(e.Outcome, e.ErrorCode, e.ErrorMessage);
                break;
            case PendingEventEnqueued e:
                _state.AddPendingOperation(e.OpKey, e.Payload);
                break;
            case PendingEventDrained e:
                _state.RemovePendingOperation(e.OpKey);
                break;
            case PendingEventApplied e:
                _state.RecordAppliedOperation(e.OpKey, e.Result);
                break;
            case InstanceMigrated e:
                _state.ApplyMigration(e.MigrationId, e.SourceProcessDefinitionId, e.TargetProcessDefinitionId, e.Mappings, e.OccurredAt);
                break;
            case UserTaskRedefined e:
                _state.UserTasks[e.ActivityInstanceId].Redefine(e.Assignee, e.CandidateGroups, e.CandidateUsers, e.ExpectedOutputVariables);
                break;
            default:
                throw new InvalidOperationException($"Unknown domain event type: {@event.GetType().Name}");
        }
    }

    private void ApplyWorkflowStarted(WorkflowStarted e)
    {
        _state.Initialize(e.InstanceId, e.ProcessDefinitionId, e.RootVariablesId, e.OccurredAt, e.BusinessId);
    }

    private void ApplyActivitySpawned(ActivitySpawned e)
    {
        var entry = new ActivityInstanceEntry(
            e.ActivityInstanceId,
            e.ActivityId,
            _state.Id,
            e.ScopeId);

        entry.SetCreatedAt(e.OccurredAt);
        entry.SetActivityType(e.ActivityType);
        entry.SetVariablesId(e.VariablesId);

        if (e.TokenId.HasValue)
            entry.SetTokenId(e.TokenId.Value);

        if (e.MultiInstanceIndex.HasValue)
            entry.SetMultiInstanceIndex(e.MultiInstanceIndex.Value);

        _state.AddEntries([entry]);
    }

    private void ApplyActivityInputScopeOpened(ActivityInputScopeOpened e)
    {
        var entry = _state.GetEntry(e.ActivityInstanceId);
        _state.AddChildVariableState(e.ScopeId, entry.VariablesId);
        _state.MergeState(e.ScopeId, e.Variables);
        entry.SetInputScopeId(e.ScopeId);
    }

    private void ApplyActivityExecutionStarted(ActivityExecutionStarted e)
    {
        _state.ExecuteEntry(e.ActivityInstanceId, e.OccurredAt);
    }

    private void ApplyActivityCompleted(ActivityCompleted e)
    {
        _state.CompleteEntry(e.ActivityInstanceId, e.Variables, e.VariablesId, e.OccurredAt);
        // Bound ScopeHostExecutionScopes growth across long-running workflows
        // by removing the host's entry on terminal transition. Idempotent on
        // replay: Remove is a no-op when the key is absent. The ActivityType
        // check is replay-safe (no dependency on _definition, which is null
        // during replay-mode reconstruction).
        if (IsSubProcessActivityType(_state.GetEntry(e.ActivityInstanceId).ActivityType))
            _state.ScopeHostExecutionScopes.Remove(e.ActivityInstanceId);
    }

    private void ApplyActivityFailed(ActivityFailed e)
    {
        _state.FailEntry(e.ActivityInstanceId, e.ErrorCode, e.ErrorMessage, e.OccurredAt);
    }

    private void ApplyActivityCancelled(ActivityCancelled e)
    {
        _state.CancelEntry(e.ActivityInstanceId, e.Reason, e.OccurredAt);
        if (IsSubProcessActivityType(_state.GetEntry(e.ActivityInstanceId).ActivityType))
            _state.ScopeHostExecutionScopes.Remove(e.ActivityInstanceId);
    }

    /// <summary>
    /// Returns true when the activity-instance entry's <c>ActivityType</c>
    /// (set from <c>GetType().Name</c> at spawn time) matches a SubProcess host
    /// — i.e. <see cref="SubProcess"/> or its sealed subclass <see cref="Transaction"/>.
    /// Used to keep the <see cref="WorkflowInstanceState.ScopeHostExecutionScopes"/>
    /// dictionary bounded as host activity instances reach a terminal state.
    /// Replay-safe: does not touch <c>_definition</c>.
    /// </summary>
    private static bool IsSubProcessActivityType(string? activityType)
        => activityType is nameof(SubProcess) or nameof(Transaction) or nameof(EventSubProcess);

    private void ApplyGatewayForkTokenAdded(GatewayForkTokenAdded e)
    {
        _state.AddTokenToFork(e.ForkInstanceId, e.TokenId);
    }
}
