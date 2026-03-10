using System.Dynamic;
using Fleans.Domain.Activities;
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
                // handled in later tasks
                break;
            case ChildVariableScopeCreated e:
                // handled in later tasks
                break;
            case VariableScopeCloned e:
                // handled in later tasks
                break;
            case VariableScopesRemoved e:
                // handled in later tasks
                break;
            case ConditionSequencesAdded e:
                // handled in later tasks
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
