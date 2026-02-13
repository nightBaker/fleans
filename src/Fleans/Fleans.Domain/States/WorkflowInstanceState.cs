using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Domain.States;

// TODO: Add [GenerateSerializer] and [Id] attributes before implementing real storage.
// Fields must become public properties for Orleans serialization.
public class WorkflowInstanceState
{
    private readonly List<ActivityInstanceEntry> _activeActivities = new();
    private readonly List<ActivityInstanceEntry> _completedActivities = new();
    private readonly Dictionary<Guid, WorklfowVariablesState> _variableStates = new();
    private readonly Dictionary<Guid, ConditionSequenceState[]> _conditionSequenceStates = new();
    private bool _isStarted;
    private bool _isCompleted;

    public Guid InstanceId { get; internal set; }
    public DateTimeOffset? CreatedAt { get; internal set; }
    public DateTimeOffset? ExecutionStartedAt { get; internal set; }
    public DateTimeOffset? CompletedAt { get; internal set; }

    public bool IsStarted() => _isStarted;
    public bool IsCompleted() => _isCompleted;

    public IReadOnlyList<ActivityInstanceEntry> GetCompletedActivities()
        => _completedActivities;

    public IReadOnlyDictionary<Guid, WorklfowVariablesState> GetVariableStates()
        => _variableStates;

    public IReadOnlyDictionary<Guid, ConditionSequenceState[]> GetConditionSequenceStates()
        => _conditionSequenceStates;

    public IReadOnlyList<ActivityInstanceEntry> GetActiveActivities()
        => _activeActivities;

    public void StartWith(ActivityInstanceEntry entry, Guid variablesId)
    {
        _variableStates.Add(variablesId, new WorklfowVariablesState());
        _activeActivities.Add(entry);
    }

    public void Start()
    {
        if (_isStarted)
            throw new InvalidOperationException("Workflow is already started");

        _isStarted = true;
    }

    public void Complete()
    {
        if (_isCompleted)
            throw new InvalidOperationException("Workflow is already completed");

        _isCompleted = true;
    }

    public Guid AddCloneOfVariableState(Guid variableStateId)
    {
        var clonedState = new WorklfowVariablesState();
        clonedState.CloneWithNewIdFrom(_variableStates[variableStateId]);

        _variableStates.Add(clonedState.Id, clonedState);
        return clonedState.Id;
    }

    public void AddConditionSequenceStates(Guid activityInstanceId, ConditionalSequenceFlow[] sequences)
    {
        var sequenceStates = sequences.Select(sequence => new ConditionSequenceState(sequence.SequenceFlowId)).ToArray();
        _conditionSequenceStates.Add(activityInstanceId, sequenceStates);
    }

    public void RemoveActiveActivities(List<ActivityInstanceEntry> removeEntries)
    {
        _activeActivities.RemoveAll(removeEntries.Contains);
    }

    public void AddActiveActivities(IEnumerable<ActivityInstanceEntry> entries)
    {
        _activeActivities.AddRange(entries);
    }

    public void AddCompletedActivities(IEnumerable<ActivityInstanceEntry> entries)
    {
        _completedActivities.AddRange(entries);
    }

    public ActivityInstanceEntry? GetFirstActive(string activityId)
    {
        return _activeActivities.FirstOrDefault(a => a.ActivityId == activityId);
    }

    public void SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        var sequences = _conditionSequenceStates[activityInstanceId];

        var sequence = sequences.FirstOrDefault(s => s.ConditionalSequenceFlowId == sequenceId);

        if (sequence != null)
        {
            sequence.SetResult(result);
        }
        else
        {
            throw new NullReferenceException("Sequence not found");
        }
    }

    public void MergeState(Guid variablesId, ExpandoObject variables)
    {
        _variableStates[variablesId].Merge(variables);
    }
}
