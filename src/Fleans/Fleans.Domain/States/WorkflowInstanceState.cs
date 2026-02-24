using System.Dynamic;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class WorkflowInstanceState
{
    [Id(0)]
    public Guid Id { get; private set; }

    [Id(1)]
    public string? ETag { get; private set; }

    [Id(2)]
    public List<ActivityInstanceEntry> Entries { get; private set; } = new();

    [Id(3)]
    public List<WorkflowVariablesState> VariableStates { get; private set; } = new();

    [Id(4)]
    public List<ConditionSequenceState> ConditionSequenceStates { get; private set; } = new();

    [Id(5)]
    public bool IsStarted { get; private set; }

    [Id(6)]
    public bool IsCompleted { get; private set; }

    [Id(7)]
    public DateTimeOffset? CreatedAt { get; private set; }

    [Id(8)]
    public DateTimeOffset? ExecutionStartedAt { get; private set; }

    [Id(9)]
    public DateTimeOffset? CompletedAt { get; private set; }

    [Id(10)]
    public string? ProcessDefinitionId { get; private set; }

    [Id(11)]
    public Guid? ParentWorkflowInstanceId { get; private set; }

    [Id(12)]
    public string? ParentActivityId { get; private set; }

    public IEnumerable<ActivityInstanceEntry> GetActiveActivities()
        => Entries.Where(e => !e.IsCompleted);

    public IEnumerable<ActivityInstanceEntry> GetCompletedActivities()
        => Entries.Where(e => e.IsCompleted);

    public ActivityInstanceEntry? GetFirstActive(string activityId)
        => Entries.FirstOrDefault(a => a.ActivityId == activityId && !a.IsCompleted);

    public WorkflowVariablesState GetVariableState(Guid id)
        => VariableStates.FirstOrDefault(v => v.Id == id)
            ?? throw new InvalidOperationException($"Variable state '{id}' not found");

    public IEnumerable<ConditionSequenceState> GetConditionSequenceStatesForGateway(Guid gatewayActivityInstanceId)
        => ConditionSequenceStates.Where(c => c.GatewayActivityInstanceId == gatewayActivityInstanceId);

    public void StartWith(Guid id, string? processDefinitionId, ActivityInstanceEntry entry, Guid variablesId)
    {
        Id = id;
        ProcessDefinitionId = processDefinitionId;
        CreatedAt = DateTimeOffset.UtcNow;
        VariableStates.Add(new WorkflowVariablesState(variablesId, id));
        Entries.Add(entry);
    }

    public void SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId)
    {
        ParentWorkflowInstanceId = parentWorkflowInstanceId;
        ParentActivityId = parentActivityId;
    }

    public void Start()
    {
        if (IsStarted)
            throw new InvalidOperationException("Workflow is already started");
        
        ExecutionStartedAt = DateTimeOffset.UtcNow;
        IsStarted = true;
    }

    public void Complete()
    {
        if (IsCompleted)
            throw new InvalidOperationException("Workflow is already completed");

        CompletedAt = DateTimeOffset.UtcNow;
        IsCompleted = true;
    }

    public Guid AddCloneOfVariableState(Guid variableStateId)
    {
        var source = VariableStates.First(v => v.Id == variableStateId);
        WorkflowVariablesState clonedState;
        if (source.ParentVariablesId.HasValue)
        {
            clonedState = new WorkflowVariablesState(Guid.NewGuid(), Id, source.ParentVariablesId.Value);
        }
        else
        {
            clonedState = new WorkflowVariablesState(Guid.NewGuid(), Id);
        }
        clonedState.Merge(source.Variables);

        VariableStates.Add(clonedState);
        return clonedState.Id;
    }

    public Guid AddChildVariableState(Guid parentVariablesId)
    {
        var childState = new WorkflowVariablesState(Guid.NewGuid(), Id, parentVariablesId);
        VariableStates.Add(childState);
        return childState.Id;
    }

    public void AddConditionSequenceStates(Guid activityInstanceId, string[] sequenceFlowIds)
    {
        var sequenceStates = sequenceFlowIds.Select(id =>
            new ConditionSequenceState(id, activityInstanceId, Id));
        ConditionSequenceStates.AddRange(sequenceStates);
    }

    public void CompleteEntries(List<ActivityInstanceEntry> entries)
    {
        foreach (var entry in entries)
            entry.MarkCompleted();
    }

    public void AddEntries(IEnumerable<ActivityInstanceEntry> entries)
    {
        Entries.AddRange(entries);
    }

    public void SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        var sequence = ConditionSequenceStates
            .FirstOrDefault(c => c.GatewayActivityInstanceId == activityInstanceId
                && c.ConditionalSequenceFlowId == sequenceId)
            ?? throw new InvalidOperationException("Sequence not found");

        sequence.SetResult(result);
    }

    public void MergeState(Guid variablesId, ExpandoObject variables)
    {
        GetVariableState(variablesId).Merge(variables);
    }
}
