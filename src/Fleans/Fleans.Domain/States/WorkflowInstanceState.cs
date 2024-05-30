using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.States;

public class WorkflowInstanceState
{
    public bool IsStarted { get; private set; }
    public bool IsCompleted { get; private set; }

    public List<ActivityInstance> ActiveActivities { get; } = new List<ActivityInstance>();
    public List<ActivityInstance> CompletedActivities { get; } = new List<ActivityInstance>();

    public IReadOnlyDictionary<Guid, WorklfowVariablesState> VariableStates => _variableStates;
    private readonly Dictionary<Guid, WorklfowVariablesState> _variableStates = new();

    public IReadOnlyDictionary<Guid, ConditionSequenceState[]> ConditionSequenceStates => _conditionSequenceStates;
    private readonly Dictionary<Guid, ConditionSequenceState[]> _conditionSequenceStates = new();
    
    public WorkflowInstanceState(Activity startActivity)
    {
        var variablesId = Guid.NewGuid();
        _variableStates.Add(variablesId, new WorklfowVariablesState());
        ActiveActivities.Add(new ActivityInstance(startActivity, variablesId));
    }

    internal void Start()
    {
        if (IsStarted)
            throw new InvalidOperationException("Workflow is already started");

        IsStarted = true;
    }
    internal void Complete()
    {
        if (!ActiveActivities.Any())
            throw new InvalidOperationException("Workflow is already completed");

        IsCompleted = true;
    }

    internal Guid AddCloneOfVariableState(Guid variableStateId)
    {
        var newVariableStateId = Guid.NewGuid();

        var clonedState = new WorklfowVariablesState();
        clonedState.CloneFrom(_variableStates[variableStateId]);

        _variableStates.Add(newVariableStateId, clonedState);
        return newVariableStateId;
    }

    internal void AddConditionSequenceStates(Guid activityInstanceId, IEnumerable<ConditionalSequenceFlow> sequences)
    {
        var sequenceStates = sequences.Select(sequence => new ConditionSequenceState(sequence)).ToArray();
        _conditionSequenceStates.Add(activityInstanceId, sequenceStates);
    }
}
