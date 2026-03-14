using System.Dynamic;
using Fleans.Domain.Activities;

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

    [Id(13)]
    public List<GatewayForkState> GatewayForks { get; private set; } = [];

    [Id(14)]
    public List<TimerCycleTrackingState> TimerCycleTracking { get; private set; } = [];

    public IEnumerable<ActivityInstanceEntry> GetActiveActivities()
        => Entries.Where(e => !e.IsCompleted);

    public IEnumerable<ActivityInstanceEntry> GetCompletedActivities()
        => Entries.Where(e => e.IsCompleted);

    public ActivityInstanceEntry? GetFirstActive(string activityId)
        => Entries.FirstOrDefault(a => a.ActivityId == activityId && !a.IsCompleted);

    public bool HasActiveEntry(Guid activityInstanceId)
        => Entries.Any(e => e.ActivityInstanceId == activityInstanceId && !e.IsCompleted);

    public bool HasActiveChildrenInScope(Guid scopeId)
        => Entries.Any(e => e.ScopeId == scopeId && !e.IsCompleted);

    public ActivityInstanceEntry GetActiveEntry(Guid activityInstanceId)
        => Entries.FirstOrDefault(e => e.ActivityInstanceId == activityInstanceId && !e.IsCompleted)
            ?? throw new InvalidOperationException($"Active entry for activity instance '{activityInstanceId}' not found");

    public ActivityInstanceEntry GetEntry(Guid activityInstanceId)
        => Entries.FirstOrDefault(e => e.ActivityInstanceId == activityInstanceId)
            ?? throw new InvalidOperationException($"Entry for activity instance '{activityInstanceId}' not found");

    public ActivityInstanceEntry? FindEntry(Guid activityInstanceId)
        => Entries.FirstOrDefault(e => e.ActivityInstanceId == activityInstanceId);

    public List<ActivityInstanceEntry> GetEntriesInScope(Guid scopeId)
        => Entries.Where(e => e.ScopeId == scopeId).ToList();

    public Guid GetRootVariablesId()
        => VariableStates.First().Id;

    public WorkflowVariablesState GetVariableState(Guid id)
        => VariableStates.FirstOrDefault(v => v.Id == id)
            ?? throw new InvalidOperationException($"Variable state '{id}' not found");

    public IEnumerable<ConditionSequenceState> GetConditionSequenceStatesForGateway(Guid gatewayActivityInstanceId)
        => ConditionSequenceStates.Where(c => c.GatewayActivityInstanceId == gatewayActivityInstanceId);

    public void Initialize(Guid id, string? processDefinitionId, Guid variablesId)
    {
        Id = id;
        ProcessDefinitionId = processDefinitionId;
        CreatedAt = DateTimeOffset.UtcNow;
        VariableStates.Add(new WorkflowVariablesState(variablesId, id));
    }

    public void StartWith(Guid id, string? processDefinitionId, ActivityInstanceEntry entry, Guid variablesId)
    {
        Initialize(id, processDefinitionId, variablesId);
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

        GatewayForks.Clear();
        CompletedAt = DateTimeOffset.UtcNow;
        IsCompleted = true;
    }

    public Guid AddCloneOfVariableState(Guid variableStateId)
    {
        var source = VariableStates.First(v => v.Id == variableStateId);
        var clonedState = new WorkflowVariablesState(Guid.NewGuid(), Id, source.ParentVariablesId);
        clonedState.Merge(source.Variables);

        VariableStates.Add(clonedState);
        return clonedState.Id;
    }

    public void AddCloneOfVariableState(Guid newScopeId, Guid sourceScopeId)
    {
        var source = VariableStates.First(v => v.Id == sourceScopeId);
        var clonedState = new WorkflowVariablesState(newScopeId, Id, source.ParentVariablesId);
        clonedState.Merge(source.Variables);

        VariableStates.Add(clonedState);
    }

    public Guid AddChildVariableState(Guid parentVariablesId)
    {
        var childState = new WorkflowVariablesState(Guid.NewGuid(), Id, parentVariablesId);
        VariableStates.Add(childState);
        return childState.Id;
    }

    public void AddChildVariableState(Guid childId, Guid parentVariablesId)
    {
        var childState = new WorkflowVariablesState(childId, Id, parentVariablesId);
        VariableStates.Add(childState);
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
            entry.Complete();
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

    public void RemoveVariableStates(IEnumerable<Guid> variableStateIds)
    {
        var idsToRemove = new HashSet<Guid>(variableStateIds);
        VariableStates.RemoveAll(vs => idsToRemove.Contains(vs.Id));
    }

    public ExpandoObject GetMergedVariables(Guid variablesStateId)
    {
        var scopes = new List<ExpandoObject>();
        var current = GetVariableState(variablesStateId);
        while (current is not null)
        {
            scopes.Add(current.Variables);
            current = current.ParentVariablesId.HasValue
                ? GetVariableState(current.ParentVariablesId.Value)
                : null;
        }

        var merged = new ExpandoObject();
        var mergedDict = (IDictionary<string, object?>)merged;
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var dict = (IDictionary<string, object?>)scopes[i];
            foreach (var kvp in dict)
                mergedDict[kvp.Key] = kvp.Value;
        }
        return merged;
    }

    public object? GetVariable(Guid variablesStateId, string variableName)
    {
        var current = GetVariableState(variablesStateId);
        while (current is not null)
        {
            var dict = (IDictionary<string, object?>)current.Variables;
            if (dict.TryGetValue(variableName, out var value))
                return value;

            current = current.ParentVariablesId.HasValue
                ? GetVariableState(current.ParentVariablesId.Value)
                : null;
        }
        return null;
    }

    public GatewayForkState CreateGatewayFork(Guid forkInstanceId, Guid? consumedTokenId)
    {
        var fork = new GatewayForkState(forkInstanceId, consumedTokenId, Id);
        GatewayForks.Add(fork);
        return fork;
    }

    public GatewayForkState? FindGatewayFork(Guid forkInstanceId)
        => GatewayForks.FirstOrDefault(f => f.ForkInstanceId == forkInstanceId);

    public GatewayForkState GetGatewayFork(Guid forkInstanceId)
        => GatewayForks.First(f => f.ForkInstanceId == forkInstanceId);

    public GatewayForkState? FindForkByToken(Guid tokenId)
        => GatewayForks.FirstOrDefault(f => f.CreatedTokenIds.Contains(tokenId));

    public void RemoveGatewayFork(Guid forkInstanceId)
        => GatewayForks.RemoveAll(f => f.ForkInstanceId == forkInstanceId);

    public TimerDefinition? GetTimerCycleState(Guid hostActivityInstanceId, string timerActivityId)
        => TimerCycleTracking
            .FirstOrDefault(t => t.HostActivityInstanceId == hostActivityInstanceId && t.TimerActivityId == timerActivityId)
            ?.ToTimerDefinition();

    public void SetTimerCycleState(Guid hostActivityInstanceId, string timerActivityId, TimerDefinition? definition)
    {
        var existing = TimerCycleTracking
            .FirstOrDefault(t => t.HostActivityInstanceId == hostActivityInstanceId && t.TimerActivityId == timerActivityId);

        if (definition is null)
        {
            if (existing is not null)
                TimerCycleTracking.Remove(existing);
        }
        else if (existing is not null)
        {
            existing.UpdateFrom(definition);
        }
        else
        {
            TimerCycleTracking.Add(new TimerCycleTrackingState(hostActivityInstanceId, timerActivityId, definition, Id));
        }
    }
}
