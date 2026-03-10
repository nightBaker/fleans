using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Domain.Aggregates;

public record PendingActivity(Guid ActivityInstanceId, string ActivityId);

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
                // handled in later tasks
                break;
            case ActivityCancelled e:
                // handled in later tasks
                break;
            case VariablesMerged e:
                _state.MergeState(e.VariablesId, e.Variables);
                break;
            case ChildVariableScopeCreated e:
                _state.AddChildVariableState(e.ScopeId, e.ParentScopeId);
                break;
            case VariableScopeCloned e:
                // handled in later tasks
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
                // handled in later tasks
                break;
            case GatewayForkTokenAdded e:
                // handled in later tasks
                break;
            case GatewayForkRemoved e:
                // handled in later tasks
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
}
