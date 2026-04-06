using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Application.Adapters;
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
    private async Task ResolveExternalCompletions()
    {
        var definition = await GetWorkflowDefinition();

        // Collect entry IDs from ActivityCompleted events in uncommitted events.
        // These are entries completed externally by the aggregate method call.
        var completedIds = _execution!.GetUncommittedEvents()
            .OfType<ActivityCompleted>()
            .Select(e => e.ActivityInstanceId)
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
            if (pending.Count == 0) break;

            var newlyCompletedEntryIds = new List<Guid>();

            foreach (var p in pending)
            {
                var entry = State.GetActiveEntry(p.ActivityInstanceId);
                var adapter = new ActivityExecutionContextAdapter(entry);
                var scopeDef = definition.GetScopeForActivity(p.ActivityId);
                var currentActivity = scopeDef.GetActivity(p.ActivityId);

                LogExecutingActivity(p.ActivityId, currentActivity.GetType().Name);

                var commands = await currentActivity.ExecuteAsync(this, adapter, scopeDef);

                // Route adapter intent through aggregate's Emit/Apply path
                if (adapter.WasExecuted)
                    _execution.MarkExecuting(entry.ActivityInstanceId);

                if (adapter.PendingMultiInstanceTotal.HasValue)
                    _execution.SetMultiInstanceTotal(entry.ActivityInstanceId, adapter.PendingMultiInstanceTotal.Value);

                // Process commands through aggregate -> get infrastructure effects
                var effects = _execution.ProcessCommands(commands, entry.ActivityInstanceId);
                await PerformEffects(effects);

                // Handle domain events published by activities (e.g., ExecuteScriptEvent)
                foreach (var evt in adapter.PublishedEvents)
                    await PublishDomainEvent(evt);

                // Route completion through aggregate's Emit/Apply path
                if (adapter.WasCompleted)
                {
                    _execution.MarkCompleted(entry.ActivityInstanceId, new ExpandoObject());
                    newlyCompletedEntryIds.Add(entry.ActivityInstanceId);
                }
            }

            // Compute transitions only for newly completed entries
            var completedTransitions = await ComputeTransitionsForEntries(definition, newlyCompletedEntryIds);
            if (completedTransitions.Count > 0)
                _execution.ResolveTransitions(completedTransitions);

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

            var transitions = await activity.GetNextActivities(this, adapter, scopeDef);
            if (transitions.Count > 0)
                result.Add(new CompletedActivityTransitions(
                    entry.ActivityInstanceId, entry.ActivityId, transitions));
        }

        return result;
    }

    private async Task<IReadOnlyList<Guid>> HandleScopeCompletions(IWorkflowDefinition definition)
    {
        var (scopeEffects, completedHostIds, orphanedScopeIds) = _execution!.CompleteFinishedSubProcessScopes();
        await PerformEffects(scopeEffects);

        if (completedHostIds.Count > 0)
        {
            var hostTransitions = new List<CompletedActivityTransitions>();
            foreach (var hostId in completedHostIds)
            {
                var hostEntry = State.GetEntry(hostId);
                var scopeDef = definition.GetScopeForActivity(hostEntry.ActivityId);
                var activity = scopeDef.GetActivity(hostEntry.ActivityId);

                if (activity is SubProcess)
                    LogSubProcessVariablesMerged(hostEntry.ActivityId, hostEntry.VariablesId);

                var adapter = new ActivityExecutionContextAdapter(hostEntry);

                var transitions = await activity.GetNextActivities(this, adapter, scopeDef);
                hostTransitions.Add(new CompletedActivityTransitions(
                    hostId, hostEntry.ActivityId, transitions));
            }
            _execution.ResolveTransitions(hostTransitions);
        }

        return orphanedScopeIds;
    }

    private Task PerformEffects(IReadOnlyList<IInfrastructureEffect> effects)
    {
        return _effectDispatcher.DispatchAsync(effects, new WorkflowInstanceEffectContext(this));
    }

    private async Task PublishDomainEvent(IDomainEvent domainEvent)
    {
        var eventPublisher = _grainFactory.GetGrain<IEventPublisher>(0);
        await eventPublisher.Publish(domainEvent);
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
                }
                LogProcessingPendingEvent(pending.GetType().Name);

                var effects = pending switch
                {
                    PendingChildCompleted c => _execution!.OnChildWorkflowCompleted(c.ParentActivityId, c.ChildVariables),
                    PendingChildFailed f => _execution!.OnChildWorkflowFailed(f.ParentActivityId, f.Exception),
                    PendingSignalDelivery s => _execution!.HandleSignalDelivery(s.ActivityId, s.HostActivityInstanceId),
                    PendingBoundarySignalFired b => _execution!.HandleSignalDelivery(b.BoundaryActivityId, b.HostActivityInstanceId),
                    _ => throw new InvalidOperationException($"Unknown pending event type: {pending.GetType().Name}")
                };
                await PerformEffects(effects);
                await ResolveExternalCompletions();
                await RunExecutionLoop();

                // Persist state before signaling success to the caller.
                // This ensures callers only see "completed" after state is durably stored.
                await DrainAndRaiseEvents();

                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        await DrainAndRaiseEvents();

        DisposePendingEventsTimerIfTerminal();
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
        var events = _execution!.GetUncommittedEvents();
        if (events.Count == 0) return;

        _draining = true;
        try
        {
            foreach (var evt in events)
            {
                LogEvent(evt);
                RaiseEvent(evt);
            }
            _execution.ClearUncommittedEvents();
            await ConfirmEvents();
        }
        finally
        {
            _draining = false;
        }
    }

    private void LogEvent(IDomainEvent evt)
    {
        switch (evt)
        {
            case WorkflowCompleted:
                LogStateCompleted();
                break;
            case ActivitySpawned spawned:
                LogActivitySpawned(spawned.ActivityInstanceId, spawned.ActivityId, spawned.ActivityType);
                break;
            case ActivityCompleted:
                LogStateCompleteEntries(1);
                break;
            case ActivityFailed failed:
                LogFailingActivity(failed.ActivityInstanceId.ToString());
                break;
            case ActivityCancelled cancelled:
                LogScopeChildCancelled(cancelled.ActivityInstanceId.ToString(),
                    State.FindEntry(cancelled.ActivityInstanceId)?.ScopeId ?? Guid.Empty);
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
                LogWorkflowInstanceStarted(started.InstanceId);
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
        }
    }
}
