using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans;
using System.Collections.Generic;

namespace Fleans.Domain.States;

public class WorkflowInstanceState : Grain, IWorkflowInstanceState
{    
    private readonly List<ActivityInstance> _activeActivities = new();    
    private readonly List<ActivityInstance> _completedActivities = new();    
    private readonly Dictionary<Guid, WorklfowVariablesState> _variableStates = new();
    private readonly Dictionary<Guid, ConditionSequenceState[]> _conditionSequenceStates = new();
    private bool _isStarted;
    private bool _isCompleted;

    public ValueTask<bool> IsStarted() => ValueTask.FromResult(_isStarted);
    public ValueTask<bool> IsCompleted() => ValueTask.FromResult(_isCompleted);

    public ValueTask<IReadOnlyList<ActivityInstance>> GetCompletedActivities()
        => ValueTask.FromResult(_completedActivities as IReadOnlyList<ActivityInstance>);
    public ValueTask<IReadOnlyDictionary<Guid, WorklfowVariablesState>> GetVariableStates()
        => ValueTask.FromResult(_variableStates as IReadOnlyDictionary<Guid, WorklfowVariablesState>);
    public ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates()
        => ValueTask.FromResult(_conditionSequenceStates as IReadOnlyDictionary<Guid, ConditionSequenceState[]>);

    public ValueTask<IReadOnlyList<ActivityInstance>> GetActiveActivities()
        => ValueTask.FromResult(_activeActivities as IReadOnlyList<ActivityInstance>);

    public void StartWith(Activity startActivity)
    {
        var variablesId = Guid.NewGuid();
        _variableStates.Add(variablesId, new WorklfowVariablesState());
        _activeActivities.Add(new ActivityInstance(startActivity, variablesId));
    }

    public void Start()
    {
        if (_isStarted)
            throw new InvalidOperationException("Workflow is already started");

        _isStarted = true;
    }

    public void Complete()
    {
        if (!_activeActivities.Any())
            throw new InvalidOperationException("Workflow is already completed");

        _isCompleted = true;
    }

    public ValueTask<Guid> AddCloneOfVariableState(Guid variableStateId)
    {
        var newVariableStateId = Guid.NewGuid();

        var clonedState = new WorklfowVariablesState();
        clonedState.CloneFrom(_variableStates[variableStateId]);

        _variableStates.Add(newVariableStateId, clonedState);
        return ValueTask.FromResult(newVariableStateId);
    }

    public void AddConditionSequenceStates(Guid activityInstanceId, IEnumerable<ConditionalSequenceFlow> sequences)
    {
        var sequenceStates = sequences.Select(sequence => new ConditionSequenceState(sequence)).ToArray();
        _conditionSequenceStates.Add(activityInstanceId, sequenceStates);
    }

    public void RemoveActiveActivities(List<ActivityInstance> removeInstances) => _activeActivities.RemoveAll(removeInstances.Contains);

    public void AddActiveActivities(IEnumerable<ActivityInstance> activities) => _activeActivities.AddRange(activities);

    public void AddCompletedActivities(IEnumerable<ActivityInstance> activities) => _completedActivities.AddRange(activities);
}
