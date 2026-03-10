using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
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

    public WorkflowExecution(WorkflowInstanceState state, IWorkflowDefinition definition)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public IReadOnlyList<IDomainEvent> GetUncommittedEvents() => _uncommittedEvents.AsReadOnly();

    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();

    public void Start()
    {
        if (_state.IsStarted)
            throw new InvalidOperationException("Workflow is already started.");

        var instanceId = Guid.NewGuid();

        Emit(new WorkflowStarted(instanceId, _definition.ProcessDefinitionId));

        var startActivity = _definition.Activities.OfType<StartEvent>().FirstOrDefault()
            ?? throw new InvalidOperationException("Workflow definition does not contain a StartEvent.");

        var variablesId = _state.VariableStates.First().Id;

        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: startActivity.ActivityId,
            ActivityType: startActivity.GetType().Name,
            VariablesId: variablesId,
            ScopeId: null,
            MultiInstanceIndex: null,
            TokenId: null));
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
        var entry = _state.GetActiveEntry(activityInstanceId);
        Emit(new ActivityExecutionStarted(activityInstanceId));
    }

    public void MarkCompleted(Guid activityInstanceId, ExpandoObject variables)
    {
        var entry = _state.GetActiveEntry(activityInstanceId);
        Emit(new ActivityCompleted(activityInstanceId, entry.VariablesId, variables));
    }

    public void ResolveTransitions(IReadOnlyList<CompletedActivityTransitions> completedTransitions)
    {
        foreach (var completed in completedTransitions)
        {
            var completedEntry = _state.Entries.First(
                e => e.ActivityInstanceId == completed.ActivityInstanceId);

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
                        existingEntry.ResetExecuting();
                        continue;
                    }
                }

                // Determine variables ID (clone if needed)
                Guid variablesId;
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
                Guid? tokenId = ResolveToken(transition, completedEntry);

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
                var existingFork = _state.GatewayForks.FirstOrDefault(
                    f => f.ForkInstanceId == sourceEntry.ActivityInstanceId);
                if (existingFork is null)
                {
                    Emit(new GatewayForkCreated(sourceEntry.ActivityInstanceId, sourceEntry.TokenId));
                }
                Emit(new GatewayForkTokenAdded(sourceEntry.ActivityInstanceId, newTokenId));

                return newTokenId;

            case TokenAction.RestoreParent:
                if (sourceEntry.TokenId.HasValue)
                {
                    var fork = _state.FindForkByToken(sourceEntry.TokenId.Value);
                    if (fork?.ConsumedTokenId.HasValue == true)
                    {
                        var restoredTokenId = fork.ConsumedTokenId.Value;
                        Emit(new GatewayForkRemoved(fork.ForkInstanceId));
                        return restoredTokenId;
                    }
                    // No fork found or no consumed token - inherit the token
                    return sourceEntry.TokenId.Value;
                }
                return null;

            default: // TokenAction.Inherit
                return sourceEntry.TokenId;
        }
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
                    Emit(new WorkflowCompleted());
                    break;

                case SpawnActivityCommand spawn:
                    ProcessSpawnActivity(spawn, activityInstanceId);
                    break;

                case OpenSubProcessCommand openSub:
                    ProcessOpenSubProcess(openSub);
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
                        signal.SignalName, _state.Id, activityInstanceId));
                    break;

                case StartChildWorkflowCommand startChild:
                    effects.Add(ProcessStartChildWorkflow(startChild, activityInstanceId));
                    break;

                case AddConditionsCommand conditions:
                    effects.AddRange(ProcessAddConditions(conditions, activityInstanceId));
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

    private void ProcessOpenSubProcess(OpenSubProcessCommand openSub)
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
            ScopeId: null,
            MultiInstanceIndex: null,
            TokenId: null));
    }

    private SubscribeMessageEffect ProcessRegisterMessage(
        RegisterMessageCommand msg, Guid activityInstanceId)
    {
        var messageDef = _definition.Messages.First(m => m.Id == msg.MessageDefinitionId);

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
            _state.Id, activityInstanceId);
    }

    private StartChildWorkflowEffect ProcessStartChildWorkflow(
        StartChildWorkflowCommand startChild, Guid activityInstanceId)
    {
        var callActivity = startChild.CallActivity;
        var entry = _state.GetActiveEntry(activityInstanceId);

        var childInstanceId = Guid.NewGuid();
        entry.SetChildWorkflowInstanceId(childInstanceId);

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

        var effects = new List<IInfrastructureEffect>();
        foreach (var evaluation in conditions.Evaluations)
        {
            var evalEvent = new EvaluateConditionEvent(
                _state.Id,
                _definition.WorkflowId,
                _definition.ProcessDefinitionId,
                activityInstanceId,
                _state.GetActiveEntry(activityInstanceId).ActivityId,
                evaluation.SequenceFlowId,
                evaluation.Condition);

            effects.Add(new PublishDomainEventEffect(evalEvent));
        }
        return effects;
    }

    // --- Activity Lifecycle (external entry points) ---

    public IReadOnlyList<IInfrastructureEffect> CompleteActivity(
        string activityId, Guid? activityInstanceId, ExpandoObject variables)
    {
        var entry = ResolveEntry(activityId, activityInstanceId);

        // Stale guard: if entry is already completed, ignore stale callbacks
        if (entry.IsCompleted)
            return [];

        Emit(new ActivityCompleted(entry.ActivityInstanceId, entry.VariablesId, variables));

        var effects = new List<IInfrastructureEffect>();

        // Unsubscribe boundary subscriptions on this activity
        effects.AddRange(BuildBoundaryUnsubscribeEffects(activityId, entry));

        // Cancel event-based gateway siblings
        effects.AddRange(CancelEventBasedGatewaySiblings(activityId, entry));

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

        // Search for boundary error handler
        var boundaryHandler = _definition.FindBoundaryErrorHandler(activityId, errorCode.ToString());

        if (boundaryHandler is not null)
        {
            // Boundary handler found — placeholder for Task 10 (full boundary handling)
            effects.Add(new CancelActivitySubscriptionsEffect(activityId, entry.ActivityInstanceId));
        }
        else
        {
            // No boundary handler found
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
            return _state.Entries.First(
                e => e.ActivityInstanceId == activityInstanceId.Value);
        }

        return _state.GetFirstActive(activityId)
            ?? throw new InvalidOperationException(
                $"No active entry found for activity '{activityId}'.");
    }

    private List<IInfrastructureEffect> BuildBoundaryUnsubscribeEffects(
        string activityId, ActivityInstanceEntry hostEntry)
    {
        var effects = new List<IInfrastructureEffect>();
        var scope = _definition.FindScopeForActivity(activityId);
        if (scope is null) return effects;

        // Boundary timer events
        foreach (var boundaryTimer in scope.Activities.OfType<BoundaryTimerEvent>()
            .Where(b => b.AttachedToActivityId == activityId))
        {
            effects.Add(new UnregisterTimerEffect(
                _state.Id, hostEntry.ActivityInstanceId, boundaryTimer.ActivityId));
        }

        // Boundary message events
        foreach (var boundaryMsg in scope.Activities.OfType<MessageBoundaryEvent>()
            .Where(b => b.AttachedToActivityId == activityId))
        {
            var messageDef = _definition.Messages.First(m => m.Id == boundaryMsg.MessageDefinitionId);
            var correlationKey = ResolveCorrelationKey(messageDef, hostEntry.VariablesId);
            effects.Add(new UnsubscribeMessageEffect(messageDef.Name, correlationKey));
        }

        // Boundary signal events
        foreach (var boundarySig in scope.Activities.OfType<SignalBoundaryEvent>()
            .Where(b => b.AttachedToActivityId == activityId))
        {
            var signalDef = _definition.Signals.First(s => s.Id == boundarySig.SignalDefinitionId);
            effects.Add(new UnsubscribeSignalEffect(signalDef.Name));
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
                    var messageDef = _definition.Messages.First(m => m.Id == msgCatch.MessageDefinitionId);
                    var correlationKey = ResolveCorrelationKey(messageDef, completedEntry.VariablesId);
                    effects.Add(new UnsubscribeMessageEffect(messageDef.Name, correlationKey));
                    break;

                case SignalIntermediateCatchEvent sigCatch:
                    var signalDef = _definition.Signals.First(s => s.Id == sigCatch.SignalDefinitionId);
                    effects.Add(new UnsubscribeSignalEffect(signalDef.Name));
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
        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    private void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case WorkflowStarted e:
                ApplyWorkflowStarted(e);
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
            case ActivityCancelled e:
                ApplyActivityCancelled(e);
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
                // handled in later tasks
                break;
            case ConditionSequencesAdded e:
                _state.AddConditionSequenceStates(e.GatewayInstanceId, e.SequenceFlowIds);
                break;
            case ConditionSequenceEvaluated e:
                // handled in later tasks
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
                // handled in later tasks
                break;
            default:
                throw new InvalidOperationException($"Unknown domain event type: {@event.GetType().Name}");
        }
    }

    private void ApplyWorkflowStarted(WorkflowStarted e)
    {
        var variablesId = Guid.NewGuid();
        _state.Initialize(e.InstanceId, e.ProcessDefinitionId, variablesId);
        _state.Start();
    }

    private void ApplyActivitySpawned(ActivitySpawned e)
    {
        var entry = new ActivityInstanceEntry(
            e.ActivityInstanceId,
            e.ActivityId,
            _state.Id,
            e.ScopeId);

        entry.SetActivity(e.ActivityId, e.ActivityType);
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
        var fork = _state.GatewayForks.First(f => f.ForkInstanceId == e.ForkInstanceId);
        fork.CreatedTokenIds.Add(e.TokenId);
    }
}
