using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Application.Adapters;
using Fleans.Application.WorkflowFactory;
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

        // Handle subprocess/multi-instance scope completions that may have occurred
        await HandleScopeCompletions(definition);
    }

    /// <summary>
    /// Core execution loop. Finds pending (not-yet-executing) activities,
    /// runs them via adapter, processes commands through aggregate, performs effects,
    /// computes transitions, and handles scope completions.
    /// </summary>
    private async Task RunExecutionLoop()
    {
        var definition = await GetWorkflowDefinition();

        while (true)
        {
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

                // Record state changes applied by the adapter
                if (adapter.WasExecuted)
                    _execution.RecordExternallyApplied(new ActivityExecutionStarted(entry.ActivityInstanceId));

                // Process commands through aggregate -> get infrastructure effects
                var effects = _execution.ProcessCommands(commands, entry.ActivityInstanceId);
                await PerformEffects(effects);

                // Handle domain events published by activities (e.g., ExecuteScriptEvent)
                foreach (var evt in adapter.PublishedEvents)
                    await PublishDomainEvent(evt);

                // If the adapter completed the activity, record it
                if (adapter.WasCompleted)
                {
                    _execution.RecordExternallyApplied(
                        new ActivityCompleted(entry.ActivityInstanceId, entry.VariablesId, new ExpandoObject()));
                    newlyCompletedEntryIds.Add(entry.ActivityInstanceId);
                }
            }

            // Compute transitions only for newly completed entries
            var completedTransitions = await ComputeTransitionsForEntries(definition, newlyCompletedEntryIds);
            if (completedTransitions.Count > 0)
                _execution.ResolveTransitions(completedTransitions);

            // Handle subprocess/multi-instance scope completions
            await HandleScopeCompletions(definition);

            // Persist after each iteration so partial progress survives grain deactivation
            // during long execution chains (e.g., sequential multi-instance with many items).
            await _state.WriteStateAsync();
        }
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

    private async Task HandleScopeCompletions(IWorkflowDefinition definition)
    {
        var (scopeEffects, completedHostIds) = _execution!.CompleteFinishedSubProcessScopes();
        await PerformEffects(scopeEffects);

        if (completedHostIds.Count > 0)
        {
            var hostTransitions = new List<CompletedActivityTransitions>();
            foreach (var hostId in completedHostIds)
            {
                var hostEntry = State.GetEntry(hostId);
                var scopeDef = definition.GetScopeForActivity(hostEntry.ActivityId);
                var activity = scopeDef.GetActivity(hostEntry.ActivityId);
                var adapter = new ActivityExecutionContextAdapter(hostEntry);

                var transitions = await activity.GetNextActivities(this, adapter, scopeDef);
                hostTransitions.Add(new CompletedActivityTransitions(
                    hostId, hostEntry.ActivityId, transitions));
            }
            _execution.ResolveTransitions(hostTransitions);
        }
    }

    private async Task PerformEffects(IReadOnlyList<IInfrastructureEffect> effects)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case RegisterTimerEffect timer:
                    var callbackGrain = _grainFactory.GetGrain<ITimerCallbackGrain>(
                        timer.WorkflowInstanceId, $"{timer.HostActivityInstanceId}:{timer.TimerActivityId}");
                    await callbackGrain.Activate(timer.DueTime);
                    LogTimerReminderRegistered(timer.TimerActivityId, timer.DueTime);
                    break;

                case UnregisterTimerEffect unregTimer:
                    var timerCancelGrain = _grainFactory.GetGrain<ITimerCallbackGrain>(
                        unregTimer.WorkflowInstanceId, $"{unregTimer.HostActivityInstanceId}:{unregTimer.TimerActivityId}");
                    await timerCancelGrain.Cancel();
                    break;

                case SubscribeMessageEffect subMsg:
                    await PerformMessageSubscribe(subMsg);
                    break;

                case UnsubscribeMessageEffect unsubMsg:
                    var unsubMsgKey = MessageCorrelationKey.Build(unsubMsg.MessageName, unsubMsg.CorrelationKey);
                    var unsubMsgGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(unsubMsgKey);
                    await unsubMsgGrain.Unsubscribe();
                    break;

                case SubscribeSignalEffect subSig:
                    await PerformSignalSubscribe(subSig);
                    break;

                case UnsubscribeSignalEffect unsubSig:
                    var unsubSigGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(unsubSig.SignalName);
                    await unsubSigGrain.Unsubscribe(unsubSig.WorkflowInstanceId, unsubSig.ActivityId);
                    break;

                case ThrowSignalEffect throwSig:
                    var throwSigGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(throwSig.SignalName);
                    var deliveredCount = await throwSigGrain.BroadcastSignal();
                    LogSignalThrown(throwSig.SignalName, deliveredCount);
                    break;

                case StartChildWorkflowEffect startChild:
                    await PerformStartChildWorkflow(startChild);
                    break;

                case NotifyParentCompletedEffect notifyCompleted:
                    var parentGrain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(notifyCompleted.ParentInstanceId);
                    await parentGrain.OnChildWorkflowCompleted(notifyCompleted.ParentActivityId, notifyCompleted.Variables);
                    break;

                case NotifyParentFailedEffect notifyFailed:
                    var parentFailGrain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(notifyFailed.ParentInstanceId);
                    await parentFailGrain.OnChildWorkflowFailed(notifyFailed.ParentActivityId, notifyFailed.Exception);
                    break;

                case PublishDomainEventEffect publishEvt:
                    await PublishDomainEvent(publishEvt.Event);
                    break;
            }
        }
    }

    private async Task PerformMessageSubscribe(SubscribeMessageEffect subMsg)
    {
        var grainKey = MessageCorrelationKey.Build(subMsg.MessageName, subMsg.CorrelationKey);
        var corrGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);

        try
        {
            await _state.WriteStateAsync(); // persist before external call
            await corrGrain.Subscribe(subMsg.WorkflowInstanceId, subMsg.ActivityId, subMsg.HostActivityInstanceId);
            LogMessageSubscriptionRegistered(subMsg.ActivityId, subMsg.MessageName, subMsg.CorrelationKey);
        }
        catch (Exception ex)
        {
            LogMessageSubscriptionFailed(subMsg.ActivityId, subMsg.MessageName, subMsg.CorrelationKey, ex);
            // Fail the activity through the aggregate
            var failEffects = _execution!.FailActivity(subMsg.ActivityId, subMsg.HostActivityInstanceId, ex);
            await PerformEffects(failEffects);
        }
    }

    private async Task PerformSignalSubscribe(SubscribeSignalEffect subSig)
    {
        var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(subSig.SignalName);

        try
        {
            await _state.WriteStateAsync(); // persist before external call
            await signalGrain.Subscribe(subSig.WorkflowInstanceId, subSig.ActivityId, subSig.HostActivityInstanceId);
            LogSignalSubscriptionRegistered(subSig.ActivityId, subSig.SignalName);
        }
        catch (Exception ex)
        {
            LogSignalSubscriptionFailed(subSig.ActivityId, subSig.SignalName, ex);
            var failEffects = _execution!.FailActivity(subSig.ActivityId, subSig.HostActivityInstanceId, ex);
            await PerformEffects(failEffects);
        }
    }

    private async Task PerformStartChildWorkflow(StartChildWorkflowEffect startChild)
    {
        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        var childDefinition = await factory.GetLatestWorkflowDefinition(startChild.ProcessDefinitionKey);

        var child = _grainFactory.GetGrain<IWorkflowInstanceGrain>(startChild.ChildInstanceId);

        LogStartingChildWorkflow(startChild.ProcessDefinitionKey, startChild.ChildInstanceId);

        await child.SetWorkflow(childDefinition);
        await child.SetParentInfo(this.GetPrimaryKey(), startChild.ParentActivityId);

        if (((IDictionary<string, object?>)startChild.InputVariables).Count > 0)
            await child.SetInitialVariables(startChild.InputVariables);

        await child.StartWorkflow();
    }

    private async Task PublishDomainEvent(IDomainEvent domainEvent)
    {
        var eventPublisher = _grainFactory.GetGrain<IEventPublisher>(0);
        await eventPublisher.Publish(domainEvent);
    }

    private void LogAndClearEvents()
    {
        foreach (var evt in _execution!.GetUncommittedEvents())
        {
            switch (evt)
            {
                case WorkflowCompleted:
                    LogStateCompleted();
                    break;
                case ActivitySpawned spawned:
                    LogStateAddEntries(1);
                    break;
                case ActivityCompleted completed:
                    LogStateCompleteEntries(1);
                    break;
                case ActivityFailed failed:
                    LogFailingActivity(failed.ActivityInstanceId.ToString());
                    break;
                case ActivityCancelled cancelled:
                    LogScopeChildCancelled(cancelled.ActivityInstanceId.ToString(), Guid.Empty);
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
            }
        }
        _execution.ClearUncommittedEvents();
    }
}
