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

    /// <summary>Active sub-process scopes, keyed by ScopeId.</summary>
    [Id(13)]
    public List<WorkflowScopeState> Scopes { get; private set; } = new();

    public IEnumerable<ActivityInstanceEntry> GetActiveActivities()
        => Entries.Where(e => !e.IsCompleted);

    public IEnumerable<ActivityInstanceEntry> GetCompletedActivities()
        => Entries.Where(e => e.IsCompleted);

    public ActivityInstanceEntry? GetFirstActive(string activityId)
        => Entries.FirstOrDefault(a => a.ActivityId == activityId && !a.IsCompleted);

    public ActivityInstanceEntry? GetActiveEntryByInstanceId(Guid activityInstanceId)
        => Entries.FirstOrDefault(e => e.ActivityInstanceId == activityInstanceId && !e.IsCompleted);

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
        var clonedState = new WorkflowVariablesState(Guid.NewGuid(), Id);
        clonedState.Merge(source.Variables);

        VariableStates.Add(clonedState);
        return clonedState.Id;
    }

    /// <summary>
    /// Creates a new child variable scope whose reads fall back to the parent scope.
    /// Writes in the child scope shadow the parent (local scope only).
    /// </summary>
    public Guid CreateChildVariableScope(Guid parentVariablesId)
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

    // ── Scope management ─────────────────────────────────────────────────────

    /// <summary>Opens a new sub-process scope and returns it.</summary>
    public WorkflowScopeState OpenScope(
        Guid scopeId,
        Guid? parentScopeId,
        Guid variablesId,
        string subProcessActivityId,
        Guid subProcessActivityInstanceId)
    {
        var scope = new WorkflowScopeState(
            scopeId, parentScopeId, variablesId,
            subProcessActivityId, subProcessActivityInstanceId);
        Scopes.Add(scope);
        return scope;
    }

    /// <summary>Returns the scope with the given id, or null if not found.</summary>
    public WorkflowScopeState? GetScope(Guid scopeId)
        => Scopes.FirstOrDefault(s => s.ScopeId == scopeId);

    /// <summary>Returns the scope created by the given sub-process activity instance, or null.</summary>
    public WorkflowScopeState? GetScopeBySubProcessInstance(Guid subProcessActivityInstanceId)
        => Scopes.FirstOrDefault(s => s.SubProcessActivityInstanceId == subProcessActivityInstanceId);

    /// <summary>Closes the scope on normal completion (no cancellation).</summary>
    public void CloseScope(Guid scopeId)
        => Scopes.RemoveAll(s => s.ScopeId == scopeId);

    /// <summary>
    /// Cancels the scope: drains all active children (including from nested scopes) and
    /// removes the scope. Returns the collected child instance ids so the caller can
    /// cancel the activity grains.
    /// </summary>
    public IReadOnlyList<Guid> CancelScope(Guid scopeId)
    {
        var scope = GetScope(scopeId);
        if (scope is null) return [];

        var allChildren = new List<Guid>();
        CollectAndRemoveScope(scope, allChildren);
        return allChildren;
    }

    private void CollectAndRemoveScope(WorkflowScopeState scope, List<Guid> collected)
    {
        // Recursively drain any nested scopes first
        foreach (var nested in Scopes.Where(s => s.ParentScopeId == scope.ScopeId).ToList())
        {
            CollectAndRemoveScope(nested, collected);
            Scopes.Remove(nested);
        }

        collected.AddRange(scope.DrainActiveChildren());
        Scopes.Remove(scope);
    }
}
