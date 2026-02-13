using System.Dynamic;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class WorkflowInstanceState
{
    [Id(0)]
    public List<ActivityInstanceEntry> ActiveActivities { get; private set; } = new();

    [Id(1)]
    public List<ActivityInstanceEntry> CompletedActivities { get; private set; } = new();

    [Id(2)]
    public Dictionary<Guid, WorkflowVariablesState> VariableStates { get; private set; } = new();

    [Id(3)]
    public Dictionary<Guid, ConditionSequenceState[]> ConditionSequenceStates { get; private set; } = new();

    [Id(4)]
    public bool IsStarted { get; private set; }

    [Id(5)]
    public bool IsCompleted { get; private set; }

    [Id(6)]
    public Guid InstanceId { get; internal set; }

    [Id(7)]
    public DateTimeOffset? CreatedAt { get; internal set; }

    [Id(8)]
    public DateTimeOffset? ExecutionStartedAt { get; internal set; }

    [Id(9)]
    public DateTimeOffset? CompletedAt { get; internal set; }

    [Id(10)]
    public Guid Id { get; internal set; }

    [Id(11)]
    public string? ETag { get; internal set; }

    public IReadOnlyList<ActivityInstanceEntry> GetCompletedActivities()
        => CompletedActivities;

    public IReadOnlyDictionary<Guid, WorkflowVariablesState> GetVariableStates()
        => VariableStates;

    public IReadOnlyDictionary<Guid, ConditionSequenceState[]> GetConditionSequenceStates()
        => ConditionSequenceStates;

    public IReadOnlyList<ActivityInstanceEntry> GetActiveActivities()
        => ActiveActivities;

    public void StartWith(ActivityInstanceEntry entry, Guid variablesId)
    {
        VariableStates.Add(variablesId, new WorkflowVariablesState());
        ActiveActivities.Add(entry);
    }

    public void Start()
    {
        if (IsStarted)
            throw new InvalidOperationException("Workflow is already started");

        IsStarted = true;
    }

    public void Complete()
    {
        if (IsCompleted)
            throw new InvalidOperationException("Workflow is already completed");

        IsCompleted = true;
    }

    public Guid AddCloneOfVariableState(Guid variableStateId)
    {
        var clonedState = new WorkflowVariablesState();
        clonedState.Merge(VariableStates[variableStateId].Variables);

        var newId = Guid.NewGuid();
        VariableStates.Add(newId, clonedState);
        return newId;
    }

    public void AddConditionSequenceStates(Guid activityInstanceId, string[] sequenceFlowIds)
    {
        var sequenceStates = sequenceFlowIds.Select(id => new ConditionSequenceState(id)).ToArray();
        ConditionSequenceStates.Add(activityInstanceId, sequenceStates);
    }

    public void RemoveActiveActivities(List<ActivityInstanceEntry> removeEntries)
    {
        ActiveActivities.RemoveAll(removeEntries.Contains);
    }

    public void AddActiveActivities(IEnumerable<ActivityInstanceEntry> entries)
    {
        ActiveActivities.AddRange(entries);
    }

    public void AddCompletedActivities(IEnumerable<ActivityInstanceEntry> entries)
    {
        CompletedActivities.AddRange(entries);
    }

    public ActivityInstanceEntry? GetFirstActive(string activityId)
    {
        return ActiveActivities.FirstOrDefault(a => a.ActivityId == activityId);
    }

    public void SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        var sequences = ConditionSequenceStates[activityInstanceId];

        var sequence = sequences.FirstOrDefault(s => s.ConditionalSequenceFlowId == sequenceId);

        if (sequence != null)
        {
            sequence.SetResult(result);
        }
        else
        {
            throw new InvalidOperationException("Sequence not found");
        }
    }

    public void MergeState(Guid variablesId, ExpandoObject variables)
    {
        VariableStates[variablesId].Merge(variables);
    }
}
