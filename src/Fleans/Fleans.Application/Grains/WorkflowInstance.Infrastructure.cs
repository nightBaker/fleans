using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Application.Adapters;
using Fleans.Application.Observability;
using Fleans.Application.Scripts;
using Orleans;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    /// <summary>
    /// After an aggregate method (CompleteActivity, HandleTimerFired, etc.) externally
    /// completes entries, this method computes transitions for those entries and resolves them.
    /// Must be called between the aggregate method and RunExecutionLoop so that
    /// newly-spawned successor activities are picked up by the loop.
    /// </summary>
    private async Task ResolveExternalCompletions(IReadOnlySet<Guid>? alreadyTransitioned = null)
    {
        var definition = await GetWorkflowDefinition();

        // Collect entry IDs from ActivityCompleted events in uncommitted events, except the ones
        // already transitioned. These are entries completed externally by the aggregate method call.
        var completedIds = _execution!.GetUncommittedEvents()
            .OfType<ActivityCompleted>()
            .Select(e => e.ActivityInstanceId)
            .Where(id => alreadyTransitioned is null || !alreadyTransitioned.Contains(id))
            .ToList();

        if (completedIds.Count > 0)
        {
            var transitions = await ComputeTransitionsForEntries(definition, completedIds);
            if (transitions.Count > 0)
                _execution.ResolveTransitions(transitions);
        }

        // Handle subprocess/multi-instance scope completions that may have occurred.
        // Orphaned scope IDs are intentionally discarded here (not in the main RunExecutionLoop):
        // ResolveExternalCompletions runs *before* RunExecutionLoop on every external entry point
        // (CompleteActivity, FailActivity, message/signal delivery, etc.). The subsequent
        // RunExecutionLoop call will re-invoke HandleScopeCompletions on every iteration and
        // accumulate any still-orphaned scope IDs into its own list, which is then drained via
        // RemoveVariableScopes after the loop exits. Removing scopes here would risk pulling
        // them out from under transitions that the loop is about to compute.
        await HandleScopeCompletions(definition);
    }

    /// <summary>
    /// Core execution loop. Finds pending (not-yet-executing) activities,
    /// runs them via adapter, processes commands through aggregate, performs effects,
    /// computes transitions, and handles scope completions.
    /// </summary>
    private const int MaxExecutionLoopIterations = 1000;
    private const int FlushThreshold = 20;

    private async Task RunExecutionLoop()
    {
        var definition = await GetWorkflowDefinition();
        var iteration = 0;
        var allOrphanedScopeIds = new List<Guid>();

        while (true)
        {
            if (++iteration > MaxExecutionLoopIterations)
                throw new InvalidOperationException(
                    $"Execution loop exceeded {MaxExecutionLoopIterations} iterations — possible infinite loop in workflow definition.");
            var pending = _execution!.GetPendingActivities();
            if (pending.Count == 0)
            {
                if (_pendingConditionEvaluations.Count == 0) break;
                // Quiescent: evaluate the queued conditions, then run what their results unblocked.
                // All of them in one pass, so a wide fan-out of gateways costs one loop iteration.
                while (_pendingConditionEvaluations.Count > 0)
                    await EvaluateNextCondition();
                continue;
            }

            var newlyCompletedEntryIds = new List<Guid>();
            var eventCountBeforeIteration = _execution.GetUncommittedEvents().Count;

            foreach (var p in pending)
            {
                // Input mappings open the activity's local scope before it executes; one that
                // fails to evaluate fails the activity, which then does not execute.
                var (inputsFailed, inputFailureEffects) = _execution.MapInputs(p.ActivityInstanceId);
                if (inputsFailed)
                {
                    await PerformEffects(inputFailureEffects);
                    continue;
                }

                var entry = State.GetActiveEntry(p.ActivityInstanceId);
                var adapter = new ActivityExecutionContextAdapter(entry);
                var scopeDef = definition.GetScopeForActivity(p.ActivityId);
                var currentActivity = scopeDef.GetActivity(p.ActivityId);

                LogExecutingActivity(p.ActivityId, currentActivity.GetType().Name);

                if (currentActivity is Transaction)
                    LogTransactionScopeOpened(currentActivity.ActivityId, entry.ActivityInstanceId, entry.ScopeId);

                var commands = await currentActivity.ExecuteAsync(this, adapter, scopeDef);

                // Route adapter intent through aggregate's Emit/Apply path
                if (adapter.WasExecuted)
                    _execution.MarkExecuting(entry.ActivityInstanceId);

                if (adapter.PendingMultiInstanceTotal.HasValue)
                    _execution.SetMultiInstanceTotal(entry.ActivityInstanceId, adapter.PendingMultiInstanceTotal.Value);

                // Process commands through aggregate -> get infrastructure effects
                var effects = _execution.ProcessCommands(commands, entry.ActivityInstanceId);
                var escalationParentResult = await PerformEffects(effects);

                // Log escalation thrown if a ThrowEscalationCommand was in the batch
                var throwEsc = commands.OfType<ThrowEscalationCommand>().FirstOrDefault();
                if (throwEsc is not null)
                    LogEscalationThrown(throwEsc.EscalationCode, p.ActivityId, State.Id);

                // If an escalation was thrown and the parent grain cancelled the child,
                // terminate this workflow and break out of the execution loop.
                if (escalationParentResult == EscalationHandledResult.Cancelled)
                {
                    _execution.TerminateForParentEscalationCancellation();
                    return;
                }

                // Handle domain events published by activities. A script goes straight to the
                // executor grain once this turn is journaled, not through a stream.
                foreach (var evt in adapter.PublishedEvents)
                {
                    if (evt is ExecuteScriptEvent script)
                        _scriptsToDispatch.Add(script);
                    else if (evt is ExecuteCustomTaskEvent customTask)
                        await PublishCustomTask(customTask);
                    else
                        await PublishDomainEvent(evt);
                }

                // Route completion through aggregate's Emit/Apply path
                if (adapter.WasCompleted)
                {
                    // Output mappings that fail to evaluate fail the activity instead.
                    var outputFailureEffects = _execution.MarkCompleted(entry.ActivityInstanceId, new ExpandoObject());
                    if (outputFailureEffects.Count > 0)
                        await PerformEffects(outputFailureEffects);
                    newlyCompletedEntryIds.Add(entry.ActivityInstanceId);
                }
            }

            // Capture entries completed by the aggregate during command processing
            // (e.g., compensation thrower completed immediately when walk has zero handlers).
            foreach (var completedEvt in _execution.GetUncommittedEvents()
                .Skip(eventCountBeforeIteration)
                .OfType<ActivityCompleted>())
            {
                if (!newlyCompletedEntryIds.Contains(completedEvt.ActivityInstanceId))
                    newlyCompletedEntryIds.Add(completedEvt.ActivityInstanceId);
            }

            // Compute transitions only for newly completed entries
            var completedTransitions = await ComputeTransitionsForEntries(definition, newlyCompletedEntryIds);
            if (completedTransitions.Count > 0)
                _execution.ResolveTransitions(completedTransitions);

            // Evaluate conditional event watchers after activity completions.
            // NOTE: Conditional watcher evaluation is intentionally placed here (inside RunExecutionLoop)
            // rather than in ResolveExternalCompletions. Variables merged by external handlers are visible
            // to watchers in this loop iteration. The loop continues until quiescent, so no evaluations
            // are missed. If an external "patch variables" API is added, it must call RunExecutionLoop
            // or a dedicated hook to trigger watcher evaluation.
            if (newlyCompletedEntryIds.Count > 0)
            {
                var watcherCompletedIds = await EvaluateConditionalWatchers();
                if (watcherCompletedIds.Count > 0)
                {
                    var watcherTransitions = await ComputeTransitionsForEntries(definition, watcherCompletedIds);
                    if (watcherTransitions.Count > 0)
                        _execution.ResolveTransitions(watcherTransitions);
                }
            }

            // Handle subprocess/multi-instance scope completions
            var orphanedScopes = await HandleScopeCompletions(definition);
            allOrphanedScopeIds.AddRange(orphanedScopes);

            // Flush periodically during long execution chains to bound durability loss.
            // Most workflows (< 20 steps) complete in a single batch and persist at the
            // end via the caller's DrainAndRaiseEvents. Writes before external calls
            // (PerformMessageSubscribe/PerformSignalSubscribe) remain unconditional.
            if (iteration % FlushThreshold == 0)
                await DrainAndRaiseEvents();
        }

        // Remove orphaned sub-process child variable scopes after the execution loop
        // completes. Scopes must persist during the loop because activities spawned by
        // sub-process completion transitions still reference them. The VariableScopesRemoved
        // event raised by the aggregate is logged via LogEvent → LogVariableScopesRemoved.
        if (allOrphanedScopeIds.Count > 0)
            _execution!.RemoveVariableScopes(allOrphanedScopeIds);

        // Deferred workflow completion: if a CompleteWorkflowCommand was deferred earlier
        // (e.g., EndEvent fired while other activities were still active), and all activities
        // have since completed or been cancelled, complete the workflow now.
        var deferredEffects = _execution!.TryDeferredWorkflowCompletion();
        if (deferredEffects.Count > 0)
            await PerformEffects(deferredEffects);

        // Stalled-by-failure termination (#762): if an unhandled activity failure consumed the
        // last live token, fail the instance visibly instead of leaving it neither active,
        // completed, nor failed. Runs after deferred completion so a completed root end event
        // still wins. The WorkflowFailed event is logged via LogEvent → LogWorkflowFailed.
        var stalledEffects = _execution.TryFailStalledWorkflow();
        if (stalledEffects.Count > 0)
            await PerformEffects(stalledEffects);
    }

    /// <summary>
    /// Computes transitions for specific completed entries.
    /// </summary>
    private async Task<List<CompletedActivityTransitions>> ComputeTransitionsForEntries(
        IWorkflowDefinition definition, List<Guid> completedEntryIds)
    {
        var result = new List<CompletedActivityTransitions>();

        foreach (var completedId in completedEntryIds)
        {
            var entry = State.GetEntry(completedId);
            if (entry.IsCancelled || entry.ErrorCode is not null) continue;

            var scopeDef = definition.FindScopeForActivity(entry.ActivityId);
            if (scopeDef is null) continue;

            var activity = scopeDef.GetActivity(entry.ActivityId);
            var adapter = new ActivityExecutionContextAdapter(entry);

            if (activity is EndEvent && scopeDef is EventSubProcess esp && entry.ScopeId is Guid espHostId)
                LogEventSubProcessHandlerEndEventFired(esp.ActivityId, entry.ActivityId, espHostId);

            var transitions = await activity.GetNextActivities(this, adapter, scopeDef);
            if (transitions.Count > 0)
                result.Add(new CompletedActivityTransitions(
                    entry.ActivityInstanceId, entry.ActivityId, transitions));
        }

        return result;
    }

    private async Task<IReadOnlyList<Guid>> HandleScopeCompletions(IWorkflowDefinition definition)
    {
        var eventCountBefore = _execution!.GetUncommittedEvents().Count;

        // Detect completed CancelEndEvents inside active Transaction scopes and initiate the
        // cancel flow (outcome=Cancelled, scope children cancelled, compensation walk started)
        // before CompleteFinishedSubProcessScopes can auto-complete the Transaction as Completed.
        var cancelEventCountBefore = _execution.GetUncommittedEvents().Count;
        var cancelEffects = _execution.InitiateTransactionCancelFlowIfNeeded();
        var hazardEffects = _execution.InitiateTransactionHazardFlowIfNeeded();
        // Log each transaction outcome just initiated
        var postCancelEvents = _execution.GetUncommittedEvents().Skip(cancelEventCountBefore).ToList();
        foreach (var cancelledTx in postCancelEvents.OfType<TransactionOutcomeSet>()
            .Where(e => e.Outcome == TransactionOutcome.Cancelled))
        {
            var txEntry = State.FindEntry(cancelledTx.TransactionInstanceId);
            LogTransactionCancelInitiated(cancelledTx.TransactionInstanceId, txEntry?.ActivityId ?? "unknown");
            var abortCount = postCancelEvents.OfType<CompensationWalkAborted>().Count();
            if (abortCount > 0)
                LogAbortingDescendantWalks(abortCount, cancelledTx.TransactionInstanceId);
        }
        foreach (var hazardTx in postCancelEvents.OfType<TransactionOutcomeSet>()
            .Where(e => e.Outcome == TransactionOutcome.Hazard))
        {
            var txEntry = State.FindEntry(hazardTx.TransactionInstanceId);
            LogTransactionHazardInitiated(hazardTx.TransactionInstanceId, txEntry?.ActivityId ?? "unknown", hazardTx.ErrorCode ?? "?");
            LogHazardCascadeToOuter(hazardTx.TransactionInstanceId, hazardTx.ErrorMessage);
        }
        if (cancelEffects.Count > 0)
            await PerformEffects(cancelEffects);
        if (hazardEffects.Count > 0)
            await PerformEffects(hazardEffects);

        var (scopeEffects, completedHostIds, orphanedScopeIds) = _execution!.CompleteFinishedSubProcessScopes();

        // Advance compensation walk if the current handler has finished.
        // Called after scope completions so that any SubProcess-scoped handler completions
        // are processed before we try to spawn the next compensation handler.
        var completedThrowerId = _execution.AdvanceCompensationWalkIfHandlerCompleted();
        if (completedThrowerId.HasValue)
        {
            var throwerTransitions = await ComputeTransitionsForEntries(definition, [completedThrowerId.Value]);
            if (throwerTransitions.Count > 0)
                _execution.ResolveTransitions(throwerTransitions);
        }

        // Detect root EventSubProcess completion — it emits WorkflowCompleted directly
        // (no outgoing flow), which is a second exit point for workflow completion.
        var newEvents = _execution.GetUncommittedEvents().Skip(eventCountBefore);
        if (newEvents.OfType<WorkflowCompleted>().Any())
        {
            var espId = completedHostIds
                .Select(id => State.FindEntry(id))
                .Where(e => e is not null && e.ScopeId is null)
                .Select(e => e!.ActivityId)
                .FirstOrDefault() ?? "unknown";
            LogRootEventSubProcessCompleted(espId);
        }

        await PerformEffects(scopeEffects);

        // Activate Error Boundaries for Hazard TX hosts whose compensation walk just completed
        var hazardBoundaryCountBefore = _execution.GetUncommittedEvents().Count;
        var hazardBoundaryEffects = _execution.ActivatePendingHazardBoundaries();
        if (hazardBoundaryEffects.Count > 0 || _execution.GetUncommittedEvents().Count > hazardBoundaryCountBefore)
        {
            var postHazardEvents = _execution.GetUncommittedEvents().Skip(hazardBoundaryCountBefore).ToList();
            foreach (var failedTx in postHazardEvents.OfType<ActivityFailed>())
            {
                var txEntry = State.FindEntry(failedTx.ActivityInstanceId);
                if (txEntry is not null)
                    LogTransactionHazardHostFailed(failedTx.ActivityInstanceId, failedTx.ErrorCode);
            }
            foreach (var cancelledTx in postHazardEvents.OfType<ActivityCancelled>())
            {
                var spawned = postHazardEvents.OfType<ActivitySpawned>().FirstOrDefault();
                if (spawned is not null)
                    LogTransactionHazardBoundaryActivated(cancelledTx.ActivityInstanceId, spawned.ActivityId);
            }
            if (hazardBoundaryEffects.Count > 0)
                await PerformEffects(hazardBoundaryEffects);
        }

        if (completedHostIds.Count > 0)
        {
            var hostTransitions = new List<CompletedActivityTransitions>();
            foreach (var hostId in completedHostIds)
            {
                var hostEntry = State.GetEntry(hostId);
                var scopeDef = definition.GetScopeForActivity(hostEntry.ActivityId);
                var activity = scopeDef.GetActivity(hostEntry.ActivityId);

                if (activity is Transaction tx)
                {
                    var outcome = State.TransactionOutcomes.GetValueOrDefault(hostId);
                    if (outcome?.Outcome == TransactionOutcome.Cancelled)
                    {
                        // Cancel path: route to the CancelBoundaryEvent attached to the Transaction.
                        // Transaction variables are NOT propagated to the parent scope (BPMN spec).
                        LogTransactionCancelBoundaryTaken(hostId, tx.ActivityId);
                        var cancelEffects2 = _execution.ActivateCancelBoundaryEvent(hostEntry, scopeDef);
                        await PerformEffects(cancelEffects2);
                        continue; // skip normal GetNextActivities routing
                    }
                    LogTransactionCompleted(hostId, tx.ActivityId);
                }
                else if (activity is SubProcess)
                    LogSubProcessVariablesMerged(hostEntry.ActivityId, hostEntry.VariablesId);
                else if (activity is EventSubProcess)
                    LogEventSubProcessHostCompleted(hostEntry.ActivityId, hostId);

                var adapter = new ActivityExecutionContextAdapter(hostEntry);

                var transitions = await activity.GetNextActivities(this, adapter, scopeDef);
                hostTransitions.Add(new CompletedActivityTransitions(
                    hostId, hostEntry.ActivityId, transitions));
            }
            _execution.ResolveTransitions(hostTransitions);
        }

        return orphanedScopeIds;
    }

    private async Task<List<Guid>> EvaluateConditionalWatchers()
    {
        var completedEntryIds = new List<Guid>();
        var watchers = State.ConditionalWatchers.ToList();
        if (watchers.Count == 0) return completedEntryIds;

        var definition = await GetWorkflowDefinition();

        foreach (var watcher in watchers)
        {
            if (!State.ConditionalWatchers.Any(w => w.ActivityInstanceId == watcher.ActivityInstanceId))
                continue;

            try
            {
                var variables = State.GetMergedVariables(watcher.VariablesId);
                var result = await _conditionEvaluator.Evaluate(watcher.ConditionExpression, variables);

                var activity = definition.GetActivityAcrossScopes(watcher.ActivityId);
                var isNonInterruptingBoundary = activity is ConditionalBoundaryEvent cb && !cb.IsInterrupting;

                if (isNonInterruptingBoundary)
                {
                    if (result && !watcher.LastEvaluatedResult)
                    {
                        LogConditionalWatcherFired(watcher.ConditionExpression, watcher.ActivityId);
                        var effects = _execution!.FireConditionalWatcher(watcher.ActivityInstanceId, watcher.ActivityId);
                        await PerformEffects(effects);
                    }
                    if (result != watcher.LastEvaluatedResult)
                        _execution!.UpdateConditionalWatcherResult(watcher.ActivityInstanceId, result);
                }
                else
                {
                    if (result)
                    {
                        LogConditionalWatcherFired(watcher.ConditionExpression, watcher.ActivityId);
                        var effects = _execution!.FireConditionalWatcher(watcher.ActivityInstanceId, watcher.ActivityId);
                        await PerformEffects(effects);

                        if (activity is ConditionalIntermediateCatchEvent)
                            completedEntryIds.Add(watcher.ActivityInstanceId);
                    }
                }
            }
            catch (Exception ex)
            {
                LogConditionalExpressionEvaluationFailed(watcher.ConditionExpression, watcher.ActivityId, ex.Message);
                // Surface the error by failing the watcher's activity instance
                // so operators can diagnose stuck workflows.
                var failEffects = _execution!.FailActivity(
                    watcher.ActivityId, watcher.ActivityInstanceId,
                    new InvalidOperationException($"Condition expression error: {ex.Message}", ex));
                await PerformEffects(failEffects);
                completedEntryIds.Add(watcher.ActivityInstanceId);
            }
        }

        return completedEntryIds;
    }

    private async Task<EscalationHandledResult?> PerformEffects(IReadOnlyList<IInfrastructureEffect> effects)
    {
        var context = new WorkflowInstanceEffectContext(this);
        await _effectDispatcher.DispatchAsync(QueueConditionEvaluations(effects), context);
        return context.EscalationParentResult;
    }

    /// <summary>
    /// Takes the condition evaluations out of <paramref name="effects"/> and queues them for
    /// <see cref="EvaluateNextCondition"/>: conditions are evaluated in this grain, not published.
    /// </summary>
    private IReadOnlyList<IInfrastructureEffect> QueueConditionEvaluations(IReadOnlyList<IInfrastructureEffect> effects)
    {
        if (!effects.Any(IsConditionEvaluation))
            return effects;

        var rest = new List<IInfrastructureEffect>(effects.Count);
        foreach (var effect in effects)
        {
            if (IsConditionEvaluation(effect))
                _pendingConditionEvaluations.Enqueue(((PublishDomainEventEffect)effect).Event);
            else
                rest.Add(effect);
        }
        return rest;
    }

    private static bool IsConditionEvaluation(IInfrastructureEffect effect) =>
        effect is PublishDomainEventEffect
        {
            Event: EvaluateConditionEvent or EvaluateActivationConditionEvent or EvaluateCompletionConditionEvent
        };

    /// <summary>
    /// Evaluates the next queued condition and applies its result in this turn — what the
    /// condition's stream handler used to do in a later turn. Only a result that changed state is
    /// resolved (a false activation or completion condition changes nothing), and only the
    /// completions it caused: the turn's earlier ones are transitioned already (#837).
    /// </summary>
    private async Task EvaluateNextCondition()
    {
        var evaluation = _pendingConditionEvaluations.Dequeue();
        var turnCompletions = _execution!.GetUncommittedEvents()
            .OfType<ActivityCompleted>()
            .Select(e => e.ActivityInstanceId)
            .ToHashSet();

        var changed = evaluation switch
        {
            EvaluateConditionEvent c => await EvaluateGatewayCondition(c),
            EvaluateActivationConditionEvent a => await EvaluateActivationCondition(a),
            EvaluateCompletionConditionEvent m => await EvaluateCompletionCondition(m),
            _ => throw new InvalidOperationException($"Unknown condition evaluation: {evaluation.GetType().Name}")
        };

        if (changed)
            await ResolveExternalCompletions(turnCompletions);
    }

    private async Task<bool> EvaluateGatewayCondition(EvaluateConditionEvent c)
    {
        // An exclusive gateway completes on its first true flow, so the rest are not evaluated; an
        // interrupting boundary may also have cancelled the gateway meanwhile.
        if (!State.HasActiveEntry(c.ActivityInstanceId))
        {
            LogConditionEvaluationSkipped(c.ActivityId, c.SequenceFlowId);
            return false;
        }

        try
        {
            var result = await _conditionEvaluator.Evaluate(c.Condition, State.GetMergedVariables(c.VariablesId));
            LogConditionResult(c.SequenceFlowId, result);
            _execution!.CompleteConditionSequence(c.ActivityId, c.SequenceFlowId, result);
        }
        catch (Exception ex)
        {
            // Includes "all conditions false and no default flow".
            LogConditionEvaluationFailed(ex, c.ActivityId, c.Condition);
            await FailActivityInTurn(c.ActivityId, c.ActivityInstanceId, ex);
        }
        return true;
    }

    private async Task<bool> EvaluateActivationCondition(EvaluateActivationConditionEvent a)
    {
        bool result;
        try
        {
            var variables = State.GetMergedVariables(a.VariablesId);
            // The condition reads the arrived-token count as _context._nroftoken.
            ((IDictionary<string, object?>)variables)["_nroftoken"] = a.NrOfToken;
            result = await _conditionEvaluator.Evaluate(a.Condition, variables);
        }
        catch (Exception ex)
        {
            LogConditionEvaluationFailed(ex, a.ActivityId, a.Condition);
            await FailActivityInTurn(a.ActivityId, a.ActivityInstanceId, ex);
            return true;
        }

        ApplyActivationConditionResult(a.ActivityId, a.ActivityInstanceId, result);
        return result;
    }

    private async Task<bool> EvaluateCompletionCondition(EvaluateCompletionConditionEvent m)
    {
        bool result;
        try
        {
            var variables = new ExpandoObject();
            var dict = (IDictionary<string, object?>)variables;
            dict["nrOfInstances"] = m.NrOfInstances;
            dict["nrOfActiveInstances"] = m.NrOfActiveInstances;
            dict["nrOfCompletedInstances"] = m.NrOfCompletedInstances;
            result = await _conditionEvaluator.Evaluate(m.Condition, variables);
        }
        catch (Exception ex)
        {
            // Log-only: the iterations carry on and the activity completes once they all have.
            LogCompletionConditionEvaluationFailed(ex, m.HostActivityId, m.Condition);
            return false;
        }

        LogCompletionConditionResult(m.HostActivityId, m.HostActivityInstanceId, result);
        if (!result)
            return false;

        LogMultiInstanceEarlyCompletion(m.HostActivityInstanceId, m.HostActivityId);
        // Stale guard 4 inside: a host that completed meanwhile is left alone.
        await PerformEffects(_execution!.CompleteMultiInstanceEarly(m.HostActivityId, m.HostActivityInstanceId));
        return true;
    }

    /// <summary>Fails an activity from inside a turn; one no longer active is left alone.</summary>
    private async Task FailActivityInTurn(string activityId, Guid activityInstanceId, Exception exception)
    {
        if (!State.HasActiveEntry(activityInstanceId))
            return;
        await PerformEffects(_execution!.FailActivity(activityId, activityInstanceId, exception));
    }

    /// <summary>A dispatched script awaiting its result: the request, how often it was sent, and when to resend.</summary>
    private sealed record ScriptDispatch(ExecuteScriptEvent Request, int Attempt, DateTime DueAt);

    /// <summary>
    /// Sends the script tasks this turn started to the executor grain. Called once the turn's
    /// events are journaled, so a result never reaches an activation that lost the activity.
    /// </summary>
    private void DispatchQueuedScripts()
    {
        if (_scriptsToDispatch.Count == 0)
            return;

        foreach (var request in _scriptsToDispatch)
        {
            if (State.HasActiveEntry(request.ActivityInstanceId))
                DispatchScript(request, attempt: 1);
        }
        _scriptsToDispatch.Clear();
    }

    /// <summary>
    /// One-way call to a stateless executor — local on a worker/combined silo, on a worker silo
    /// from a core silo. The executor calls back <c>CompleteActivity</c> / <c>FailActivity</c>;
    /// until then the dispatch is tracked so <see cref="CheckDispatchedScripts"/> can resend it.
    /// </summary>
    private void DispatchScript(ExecuteScriptEvent request, int attempt)
    {
        var variables = State.GetMergedVariables(request.VariablesId);
        _grainFactory.GetGrain<IScriptExecutorGrain>(_evaluatorOptions.NextKey())
            .Run(request, variables)
            .Ignore();

        var timeout = _evaluatorOptions.ScriptResultTimeout;
        _dispatchedScripts[request.ActivityInstanceId] = new ScriptDispatch(request, attempt, DateTime.UtcNow + timeout);
        _scriptResultTimer ??= this.RegisterGrainTimer(
            CheckDispatchedScripts,
            state: new object(),
            options: new GrainTimerCreationOptions { DueTime = timeout / 2, Period = timeout / 2, Interleave = false });

        LogScriptDispatched(request.ActivityId, request.ActivityInstanceId, attempt);
    }

    /// <summary>
    /// The script result timeout: a dispatched script still active past its due time is dispatched
    /// again (the dispatch or its result may have been lost with a silo); after
    /// <see cref="WorkerEvaluatorOptions.ScriptDispatchAttempts"/> dispatches it fails. A repeated
    /// run is harmless — only the first result is applied (the stale guard on CompleteActivity /
    /// FailActivity). Finished scripts are dropped here.
    /// </summary>
    private async Task CheckDispatchedScripts(object state, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        foreach (var (activityInstanceId, dispatch) in _dispatchedScripts.ToList())
        {
            if (!State.HasActiveEntry(activityInstanceId))
            {
                _dispatchedScripts.Remove(activityInstanceId);
                continue;
            }
            if (now < dispatch.DueAt)
                continue;

            var activityId = dispatch.Request.ActivityId;
            if (dispatch.Attempt >= WorkerEvaluatorOptions.ScriptDispatchAttempts)
            {
                _dispatchedScripts.Remove(activityInstanceId);
                LogScriptResultTimedOut(activityId, activityInstanceId, dispatch.Attempt);
                await FailActivity(activityId, activityInstanceId, new TimeoutException(
                    $"Script task '{activityId}' got no result after {dispatch.Attempt} dispatches " +
                    $"({_evaluatorOptions.ScriptResultTimeout} each)."));
                continue;
            }

            SetWorkflowRequestContext();
            LogScriptRedispatched(activityId, activityInstanceId, dispatch.Attempt + 1);
            DispatchScript(dispatch.Request, dispatch.Attempt + 1);
        }

        if (_dispatchedScripts.Count == 0 && _scriptResultTimer is not null)
        {
            _scriptResultTimer.Dispose();
            _scriptResultTimer = null;
        }
    }

    /// <summary>
    /// On activation, dispatches the script tasks the journal shows as executing: the previous
    /// activation may have gone away before their results arrived. Never fails the activation.
    /// </summary>
    private async Task RedispatchExecutingScripts()
    {
        if (State.ProcessDefinitionId is null || State.IsCompleted)
            return;

        var executing = State.Entries
            .Where(e => e.IsExecuting && e.ActivityType == nameof(ScriptTask) && State.HasActiveEntry(e.ActivityInstanceId))
            .ToList();
        if (executing.Count == 0)
            return;

        try
        {
            var definition = await GetWorkflowDefinition();
            SetWorkflowRequestContext();
            foreach (var entry in executing)
            {
                var script = definition.GetActivityAcrossScopes(entry.ActivityId) switch
                {
                    ScriptTask s => s,
                    MultiInstanceActivity { InnerActivity: ScriptTask s } => s,
                    _ => null
                };
                if (script is null)
                    continue;

                DispatchScript(new ExecuteScriptEvent(
                    this.GetPrimaryKey(), definition.WorkflowId, definition.ProcessDefinitionId,
                    entry.ActivityInstanceId, entry.ActivityId, script.Script, script.ScriptFormat,
                    entry.ExecutionVariablesId), attempt: 1);
            }
            LogExecutingScriptsRedispatched(executing.Count);
        }
        catch (Exception ex)
        {
            // Without a dispatch the result timeout cannot recover them; the next activation retries.
            LogExecutingScriptsRedispatchFailed(ex, executing.Count);
        }
    }

    private async Task PublishDomainEvent(IDomainEvent domainEvent)
    {
        var eventPublisher = _grainFactory.GetGrain<IEventPublisher>(0);
        await eventPublisher.Publish(domainEvent);
    }

    /// <summary>
    /// Hands a custom task to its plugin stream or external-task queue (#979). A failed hand-off
    /// fails the task — no worker will ever see it — so an error boundary can catch it instead of the
    /// token waiting forever (registration failure → FailActivity, docs/conventions/invariants.md).
    /// </summary>
    private async Task PublishCustomTask(ExecuteCustomTaskEvent task)
    {
        try
        {
            await PublishDomainEvent(task);
        }
        catch (Exception ex)
        {
            LogCustomTaskPublishFailed(ex, task.ActivityId, task.TaskType);
            await FailActivityInTurn(task.ActivityId, task.ActivityInstanceId, ex);
        }
    }

    /// <summary>
    /// Drains the pending external event queue, processes each event through the aggregate,
    /// persists state after each event, and only then signals completion to the caller.
    /// Called at the end of every regular (non-[AlwaysInterleave]) grain method to ensure
    /// enqueued events are processed within the serialized grain turn.
    /// </summary>
    private async Task ProcessPendingEvents()
    {
        while (_pendingExternalEvents.TryDequeue(out var item))
        {
            var (pending, completion) = item;
            try
            {
                await EnsureExecution();
                // The buffer may still hold the turn's own completions, which RunExecutionLoop has
                // transitioned already; resolve only the ones this callback emits (#837). By id, not
                // by position: an effect's PersistStateAsync drains the buffer.
                var turnCompletions = _execution!.GetUncommittedEvents()
                    .OfType<ActivityCompleted>()
                    .Select(e => e.ActivityInstanceId)
                    .ToHashSet();
                SetWorkflowRequestContext();
                using var scope = BeginWorkflowScope();

                switch (pending)
                {
                    case PendingChildCompleted c:
                        LogChildWorkflowCompleted(c.ParentActivityId);
                        break;
                    case PendingChildFailed f:
                        LogChildWorkflowFailed(f.ParentActivityId);
                        break;
                    case PendingSignalDelivery s:
                        LogSignalDeliveryComplete(s.ActivityId);
                        break;
                    case PendingBoundarySignalFired b:
                        LogSignalDeliveryBoundary(b.BoundaryActivityId);
                        break;
                    case PendingChildEscalationRaised r:
                        LogChildEscalationRaised(r.ChildWorkflowInstanceId, r.EscalationCode);
                        break;
                }
                LogProcessingPendingEvent(pending.GetType().Name);

                if (pending is PendingChildEscalationRaised escalation)
                {
                    var (localEffects, localResult) = _execution!.HandleChildEscalationRaised(
                        escalation.EscalationInstanceId,
                        escalation.ChildWorkflowInstanceId, escalation.HostActivityId,
                        escalation.EscalationCode, escalation.Variables);
                    var parentResult = await PerformEffects(localEffects);

                    // Log escalation caught at this grain if a boundary matched
                    if (localResult == EscalationHandledResult.Cancelled)
                        LogEscalationCaught(escalation.EscalationCode, escalation.HostActivityId, isInterrupting: true);
                    else if (localResult == EscalationHandledResult.Continue)
                        LogEscalationCaught(escalation.EscalationCode, escalation.HostActivityId, isInterrupting: false);

                    EscalationHandledResult finalResult;
                    if (localResult == EscalationHandledResult.NeedsParentLookup)
                    {
                        if (parentResult is null)
                            LogEscalationParentResultMissing(escalation.EscalationCode, State.Id);
                        finalResult = parentResult ?? EscalationHandledResult.Unhandled;
                    }
                    else
                    {
                        finalResult = localResult;
                    }

                    // Drain the durable ledger entry and record the retried op's outcome (#657).
                    EmitPendingDrainedAndApplied(escalation.OperationId,
                        new Fleans.Domain.Events.PendingEventResult(finalResult));

                    escalation.Tcs.SetResult(finalResult);
                }
                else
                {
                    var effects = pending switch
                    {
                        PendingChildCompleted c => _execution!.OnChildWorkflowCompleted(c.ParentActivityId, c.ChildVariables),
                        PendingChildFailed f => _execution!.OnChildWorkflowFailed(f.ParentActivityId, f.Exception),
                        PendingSignalDelivery s => _execution!.HandleSignalDelivery(s.ActivityId, s.HostActivityInstanceId),
                        PendingBoundarySignalFired b => _execution!.HandleSignalDelivery(b.BoundaryActivityId, b.HostActivityInstanceId),
                        _ => throw new InvalidOperationException($"Unknown pending event type: {pending.GetType().Name}")
                    };
                    await PerformEffects(effects);

                    // Drain the durable ledger entry. Child-completed/failed are caller-retried and a
                    // signal delivery is stream-redelivered (#976), so also record an (empty-result)
                    // applied entry for dedup; the boundary-signal path only drains (#657).
                    if (pending is PendingChildCompleted or PendingChildFailed or PendingSignalDelivery)
                        EmitPendingDrainedAndApplied(pending.OperationId, null);
                    else
                        _execution!.EmitPendingEventDrained(pending.OperationId);
                }
                await ResolveExternalCompletions(turnCompletions);
                await RunExecutionLoop();

                // Persist state before signaling success to the caller.
                // This ensures callers only see "completed" after state is durably stored.
                await DrainAndRaiseEvents();

                _inFlightOperations.TryRemove(pending.OperationId, out _);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                _inFlightOperations.TryRemove(pending.OperationId, out _);
                completion.TrySetException(ex);
                // The escalation caller awaits its own result TCS, not `completion`.
                if (pending is PendingChildEscalationRaised failedEscalation)
                    failedEscalation.Tcs.TrySetException(ex);
            }
        }

        // Every entry point runs the execution loop, which evaluates queued conditions; this only
        // guards against a path that performed effects without it.
        if (_pendingConditionEvaluations.Count > 0)
            await RunExecutionLoop();

        await DrainAndRaiseEvents();
        DispatchQueuedScripts();

        DisposePendingEventsTimerIfTerminal();
        DeactivateIfTerminal();
    }

    /// <summary>
    /// Emits PendingEventDrained (removes from PendingOperations) plus PendingEventApplied
    /// (records the outcome in the dedup ledger for caller-retried ops). (#657)
    /// </summary>
    private void EmitPendingDrainedAndApplied(string opKey, Fleans.Domain.Events.PendingEventResult? result)
    {
        _execution!.EmitPendingEventDrained(opKey);
        _execution!.EmitPendingEventApplied(opKey, result);
    }

    /// <summary>
    /// Safety-net timer callback. Processes pending events that arrived when no regular
    /// method was executing. This is a regular grain method (not [AlwaysInterleave]),
    /// so it serializes with other regular methods.
    /// Orleans grain turn serialization (Interleave = false) ensures this callback
    /// cannot overlap with any regular grain method, so double-processing is impossible.
    /// </summary>
    private async Task ProcessPendingEventsTimer(object state, CancellationToken cancellationToken)
    {
        if (_pendingExternalEvents.IsEmpty)
        {
            DisposePendingEventsTimerIfTerminal();
            return;
        }

        await ProcessPendingEvents();
    }

    private void DisposePendingEventsTimerIfTerminal()
    {
        if (_pendingEventsTimer is not null && _pendingExternalEvents.IsEmpty && State.IsCompleted)
        {
            _pendingEventsTimer.Dispose();
            _pendingEventsTimer = null;
        }
    }

    /// <summary>
    /// A terminal instance is done: release it (state, definition reference, timers) as soon as it
    /// is idle instead of keeping it resident for the collection age (#860). Only once nothing is
    /// left to finish in memory — queued or journaled pending events, undrained events, or a
    /// projection retry. A later call reactivates it from the journal. An instance started from a
    /// definition that was never deployed (no ProcessDefinitionId — only tests do this) could not
    /// reload it, so it stays until collection as before.
    /// </summary>
    private void DeactivateIfTerminal()
    {
        if (_terminalDeactivationRequested
            || !State.IsCompleted
            || State.ProcessDefinitionId is null
            || !_pendingExternalEvents.IsEmpty
            || State.PendingOperations.Count > 0
            || HasUndrainedEvents
            || _projectionRetryTimer is not null)
            return;

        _terminalDeactivationRequested = true;
        LogTerminalDeactivation();
        DeactivateOnIdle();
    }

    /// <summary>
    /// Durably persists a pending external event (RaiseEvent(PendingEventEnqueued) + ConfirmEvents)
    /// and enqueues it to the in-memory drain queue with a fresh TCS. Serialized via _enqueueLock so
    /// the [AlwaysInterleave] enqueue methods don't race on the journal. The state mutation is applied
    /// directly (this event is independent of the _execution aggregate's uncommitted events) before
    /// the event is raised, like every other event (TransitionState applies nothing). (#657)
    /// </summary>
    private async Task EnqueueDurable(
        string opKey,
        Fleans.Domain.Events.PendingExternalEventPayload payload,
        PendingExternalEvent inMemory,
        TaskCompletionSource completion)
    {
        await _enqueueLock.WaitAsync();
        try
        {
            ThrowIfStateReloaded();

            // Record the in-flight task BEFORE persisting so a concurrent retry sees it.
            _inFlightOperations[opKey] = inMemory is PendingChildEscalationRaised esc
                ? esc.Tcs.Task
                : completion.Task;

            var evt = WorkflowExecution.Stamp(new Fleans.Domain.Events.PendingEventEnqueued(opKey, payload));
            // State is ahead of the journal until this event is appended; EnqueueInFlight keeps
            // snapshots out of that window.
            State.AddPendingOperation(opKey, payload);
            LogProcessingPendingEvent(nameof(Fleans.Domain.Events.PendingEventEnqueued));

            RaiseEvent(evt);
            await ConfirmEvents();
            LogPendingEventQueued(opKey);

            _pendingExternalEvents.Enqueue((inMemory, completion));
            EnsurePendingEventsTimerRegistered();
        }
        finally
        {
            _enqueueLock.Release();
        }
    }

    private bool TryGetInFlight(string opKey, out Task task)
    {
        if (_inFlightOperations.TryGetValue(opKey, out var t))
        {
            task = t;
            return true;
        }
        task = Task.CompletedTask;
        return false;
    }

    private bool TryGetInFlightEscalation(string opKey, out Task<EscalationHandledResult> task)
    {
        if (_inFlightOperations.TryGetValue(opKey, out var t) && t is Task<EscalationHandledResult> typed)
        {
            task = typed;
            return true;
        }
        task = Task.FromResult(EscalationHandledResult.Unhandled);
        return false;
    }

    private void EnsurePendingEventsTimerRegistered()
    {
        _pendingEventsTimer ??= this.RegisterGrainTimer(
            ProcessPendingEventsTimer,
            state: new object(),
            options: new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.Zero,
                Period = TimeSpan.FromSeconds(1),
                Interleave = false
            });
    }

    /// <summary>
    /// Drains uncommitted events from the aggregate, logs each event,
    /// raises them via JournaledGrain, and persists via ConfirmEvents.
    /// Replaces the old LogAndClearEvents + WriteStateAsync pattern.
    /// </summary>
    private async Task DrainAndRaiseEvents()
    {
        ThrowIfStateReloaded();
        var events = _execution!.GetUncommittedEvents();
        if (events.Count == 0) return;

        foreach (var evt in events)
        {
            LogEvent(evt);
            RaiseEvent(evt);
        }
        _execution.ClearUncommittedEvents();
        await ConfirmEvents();
    }

    /// <summary>
    /// Rejects the call once a re-read found the journal ahead of this activation (#845): the
    /// aggregate's state is out of date, so nothing more is applied to it or journaled from it.
    /// Checked before a call changes state and before each drain's <c>RaiseEvent</c>. A drain that
    /// already confirmed is not undone — its batch is in the journal. The exception is transient:
    /// the caller's retry (stream redelivery, <c>CallWithRetry</c>) reaches the next activation.
    /// </summary>
    private void ThrowIfStateReloaded()
    {
        if (_stateReloaded)
            throw new WorkflowInstanceStaleException(
                $"Workflow instance {this.GetPrimaryKey()} found its journal ahead of its state after a failed " +
                "or conflicting append; this activation is deactivating. Retry the call.");
    }

    private void LogEvent(WorkflowEvent evt)
    {
        switch (evt)
        {
            case WorkflowCompleted wfCompleted:
                LogStateCompleted();
                FleansDiagnostics.OnWorkflowCompleted(_workflowDefinition?.WorkflowId, State.CreatedAt, wfCompleted.OccurredAt);
                break;
            case ActivitySpawned spawned:
                LogActivitySpawned(spawned.ActivityInstanceId, spawned.ActivityId, spawned.ActivityType);
                break;
            case ActivityCompleted activityCompleted:
                LogStateCompleteEntries(1);
                RecordActivityDuration(activityCompleted.ActivityInstanceId, activityCompleted.OccurredAt);
                break;
            case ActivityFailed failed:
                LogFailingActivity(failed.ActivityInstanceId.ToString());
                RecordActivityDuration(failed.ActivityInstanceId, failed.OccurredAt);
                break;
            case ActivityCancelled cancelled:
                LogScopeChildCancelled(cancelled.ActivityInstanceId.ToString(),
                    State.FindEntry(cancelled.ActivityInstanceId)?.ScopeId ?? Guid.Empty);
                RecordActivityDuration(cancelled.ActivityInstanceId, cancelled.OccurredAt);
                break;
            case VariablesMerged merged:
                LogStateMergeState(merged.VariablesId);
                break;
            case GatewayForkCreated forkCreated:
                LogGatewayForkStateCreated(forkCreated.ForkInstanceId, forkCreated.ConsumedTokenId);
                break;
            case GatewayForkRemoved forkRemoved:
                LogGatewayForkStateRemoved(forkRemoved.ForkInstanceId);
                break;
            case ParentInfoSet parentInfo:
                LogParentInfoSet(parentInfo.ParentInstanceId, parentInfo.ParentActivityId);
                break;
            case WorkflowStarted started:
                LogWorkflowInstanceStarted(started.InstanceId, started.BusinessId ?? "-");
                FleansDiagnostics.OnWorkflowStarted();
                break;
            case ExecutionStarted:
                LogExecutionStarted();
                break;
            case ActivityExecutionStarted execStarted:
                LogActivityExecutionStarted(execStarted.ActivityInstanceId);
                break;
            case ChildVariableScopeCreated childScope:
                LogChildVariableScopeCreated(childScope.ScopeId, childScope.ParentScopeId);
                break;
            case VariableScopeCloned cloned:
                LogVariableScopeCloned(cloned.NewScopeId, cloned.SourceScopeId);
                break;
            case VariableScopesRemoved removed:
                LogVariableScopesRemoved(removed.ScopeIds.Count);
                break;
            case ActivityInputScopeOpened inputScope:
                LogActivityInputScopeOpened(inputScope.ActivityInstanceId, inputScope.ScopeId,
                    ((IDictionary<string, object?>)inputScope.Variables).Count);
                break;
            case ConditionSequencesAdded condAdded:
                LogConditionSequencesAdded(condAdded.GatewayInstanceId, condAdded.SequenceFlowIds.Length);
                break;
            case ConditionSequenceEvaluated condEval:
                LogConditionSequenceEvaluated(condEval.GatewayInstanceId, condEval.SequenceFlowId, condEval.Result);
                break;
            case GatewayForkTokenAdded tokenAdded:
                LogGatewayForkTokenAdded(tokenAdded.ForkInstanceId, tokenAdded.TokenId);
                break;
            case ActivityExecutionReset execReset:
                LogActivityExecutionReset(execReset.ActivityInstanceId);
                break;
            case ChildWorkflowLinked childLinked:
                LogChildWorkflowLinked(childLinked.ActivityInstanceId, childLinked.ChildWorkflowInstanceId);
                break;
            case MultiInstanceTotalSet miTotal:
                LogMultiInstanceTotalSet(miTotal.ActivityInstanceId, miTotal.Total);
                break;
            case UserTaskRegistered userTaskReg:
                LogUserTaskRegistered(userTaskReg.ActivityInstanceId, userTaskReg.Assignee);
                break;
            case UserTaskClaimed userTaskClaimed:
                LogUserTaskClaimed(userTaskClaimed.ActivityInstanceId, userTaskClaimed.UserId);
                break;
            case UserTaskUnclaimed userTaskUnclaimed:
                LogUserTaskUnclaimed(userTaskUnclaimed.ActivityInstanceId);
                break;
            case UserTaskUnregistered userTaskUnreg:
                LogUserTaskUnregistered(userTaskUnreg.ActivityInstanceId);
                break;
            case TimerCycleUpdated timerCycle:
                LogTimerCycleUpdated(timerCycle.HostActivityInstanceId, timerCycle.TimerActivityId, timerCycle.RemainingCycle is not null);
                break;
            case ConditionalWatcherRegistered condReg:
                LogConditionalWatcherRegistered(condReg.ConditionExpression, condReg.ActivityId);
                break;
            case ConditionalWatcherFired condFired:
                break; // Logged at the call site with expression context
            case ConditionalWatcherCleared condCleared:
                LogConditionalWatcherCleared(condCleared.ActivityInstanceId);
                break;
            case ConditionalWatcherResultUpdated:
                break; // Internal bookkeeping, no logging needed
            case CompensableActivitySnapshotRecorded snap:
                LogCompensableActivitySnapshotRecorded(snap.ActivityDefinitionId, State.NextCompensationSequence, snap.ScopeId);
                break;
            case CompensationWalkStarted walkStarted:
                LogCompensationWalkStarted(walkStarted.ScopeId, walkStarted.TargetActivityRef, walkStarted.HandlerCount);
                break;
            case CompensationHandlerSpawned handlerSpawned:
                LogCompensationHandlerSpawned(handlerSpawned.HandlerInstanceId, handlerSpawned.CompensableActivityDefinitionId, handlerSpawned.HandlerActivityId);
                break;
            case CompensationEntryMarkedCompensated entryMarked:
                LogCompensationEntryMarkedCompensated(entryMarked.ActivityDefinitionId, entryMarked.ScopeId);
                break;
            case CompensationWalkCompleted walkCompleted:
                LogCompensationWalkCompleted(walkCompleted.ScopeId);
                break;
            case CompensationWalkFailed walkFailed:
                LogCompensationHandlerFailed(walkFailed.HandlerInstanceId, walkFailed.ErrorCode, walkFailed.ErrorMessage);
                break;
            case CompensationWalkAborted walkAborted:
                LogCompensationWalkAborted(walkAborted.ScopeId, walkAborted.Reason);
                break;
            case WorkflowCancelled wfCancelled:
                LogWorkflowCancelled(wfCancelled.Reason);
                FleansDiagnostics.OnWorkflowCancelled(_workflowDefinition?.WorkflowId, State.CreatedAt, wfCancelled.OccurredAt);
                break;
            case WorkflowFailed wfFailed:
                LogWorkflowFailed(wfFailed.FailedActivityId, wfFailed.FailedActivityInstanceId, wfFailed.ErrorCode, wfFailed.ErrorMessage);
                FleansDiagnostics.OnWorkflowFailed(_workflowDefinition?.WorkflowId, State.CreatedAt, wfFailed.OccurredAt);
                break;
            case EscalationUncaughtRaised escUncaught:
                LogEscalationUncaught(escUncaught.EscalationCode, escUncaught.SourceActivityId);
                break;
        }
    }

    /// <summary>
    /// Spawn-to-end duration from the journaled times (the entry's <c>CreatedAt</c> is its
    /// <c>ActivitySpawned.OccurredAt</c>), so activities spawned before a reactivation count too.
    /// </summary>
    private void RecordActivityDuration(Guid activityInstanceId, DateTimeOffset endedAt)
    {
        var entry = State.FindEntry(activityInstanceId);
        if (entry?.CreatedAt is not { } createdAt || entry.ActivityType is not { } activityType) return;
        FleansDiagnostics.RecordActivityDuration((endedAt - createdAt).TotalMilliseconds, activityType);
    }
}
