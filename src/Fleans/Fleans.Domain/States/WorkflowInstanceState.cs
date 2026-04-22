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

    [Id(19)]
    public bool IsCancelled { get; private set; }

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
    public Dictionary<Guid, UserTaskMetadata> UserTasks { get; private set; } = new();

    [Id(15)]
    public List<TimerCycleTrackingState> TimerCycleTracking { get; private set; } = [];

    // Runtime caches — NOT serialized by Orleans, NOT persisted by EF Core.
    // Lazily rebuilt from Entries list after deserialization or grain activation.
    [field: NonSerialized]
    private Dictionary<Guid, ActivityInstanceEntry>? _entriesById;

    [field: NonSerialized]
    private HashSet<Guid>? _activeEntryIds;

    private Dictionary<Guid, ActivityInstanceEntry> EntriesById
    {
        get
        {
            if (_entriesById == null) RebuildCaches();
            return _entriesById!;
        }
    }

    private HashSet<Guid> ActiveEntryIds
    {
        get
        {
            if (_activeEntryIds == null) RebuildCaches();
            return _activeEntryIds!;
        }
    }

    private void RebuildCaches()
    {
        _entriesById = Entries.ToDictionary(e => e.ActivityInstanceId);
        _activeEntryIds = Entries
            .Where(e => !e.IsCompleted)
            .Select(e => e.ActivityInstanceId)
            .ToHashSet();
    }

    // Dirty-flag constants for collection change tracking. Uses int bitmask
    // to avoid ORLEANS0004 (code gen requires [GenerateSerializer] on custom
    // enum types). UserTasks ([Id(14)]) excluded — persisted by IUserTaskGrain.
    private const int DirtyEntries = 1;
    private const int DirtyVariableStates = 2;
    private const int DirtyConditionSequenceStates = 4;
    private const int DirtyGatewayForks = 8;
    private const int DirtyTimerCycleTracking = 16;

    // Serialized (Orleans 10 requires [Id()] on all fields) but semantically
    // transient — cleared after each WriteAsync. Defaults to 0 after snapshot
    // restore, which is correct since freshly loaded state has no pending changes.
    [Id(16)]
    private int _dirtyFlags;

    private const int DirtyComplexGatewayJoinStates = 32;

    [Id(17)]
    public List<ComplexGatewayJoinState> ComplexGatewayJoinStates { get; private set; } = [];

    // Transaction Sub-Process outcome tracking. In-memory — excluded from EF Core schema
    // (see FleanModelConfiguration). Populated from grain snapshot if present, otherwise
    // rebuilt from TransactionOutcomeSet events on activation.
    [Id(18)]
    public Dictionary<Guid, TransactionOutcomeRecord> TransactionOutcomes { get; private set; } = new();

    private const int DirtyConditionalWatchers = 64;
    private const int DirtyCompensationLog = 128;

    [Id(20)]
    public List<ConditionalEventWatcherState> ConditionalWatchers { get; private set; } = [];

    /// <summary>
    /// Append-only log of completed, compensable activities (those with a CompensationBoundaryEvent attached).
    /// Keyed implicitly by ScopeId on each entry. Used as input to compensation walks.
    /// </summary>
    [Id(23)]
    public List<CompletedActivitySnapshot> CompensationLog { get; private set; } = [];

    /// <summary>Global monotonic counter for assigning CompletedAtSequence to compensation snapshots.</summary>
    [Id(24)]
    public int NextCompensationSequence { get; private set; }

    /// <summary>Non-null while a compensation walk is in progress. At most one walk at a time.</summary>
    [Id(25)]
    public CompensationWalkState? ActiveCompensationWalk { get; private set; }

    internal int GetDirtyFlags() => _dirtyFlags;

    internal void ClearDirtyFlags() => _dirtyFlags = 0;

    public IEnumerable<ActivityInstanceEntry> GetActiveActivities()
        => ActiveEntryIds.Select(id => EntriesById[id]);

    public IEnumerable<ActivityInstanceEntry> GetCompletedActivities()
        => Entries.Where(e => e.IsCompleted);

    public ActivityInstanceEntry? GetFirstActive(string activityId)
        => ActiveEntryIds.Select(id => EntriesById[id])
            .FirstOrDefault(e => e.ActivityId == activityId);

    public bool HasActiveEntry(Guid activityInstanceId)
        => ActiveEntryIds.Contains(activityInstanceId);

    public bool HasActiveChildrenInScope(Guid scopeId)
        => ActiveEntryIds.Any(id => EntriesById[id].ScopeId == scopeId);

    public ActivityInstanceEntry GetActiveEntry(Guid activityInstanceId)
        => ActiveEntryIds.Contains(activityInstanceId)
            ? EntriesById[activityInstanceId]
            : throw new InvalidOperationException($"Active entry for activity instance '{activityInstanceId}' not found");

    public ActivityInstanceEntry GetEntry(Guid activityInstanceId)
        => EntriesById.TryGetValue(activityInstanceId, out var entry)
            ? entry
            : throw new InvalidOperationException($"Entry for activity instance '{activityInstanceId}' not found");

    public ActivityInstanceEntry? FindEntry(Guid activityInstanceId)
        => EntriesById.GetValueOrDefault(activityInstanceId);

    public List<ActivityInstanceEntry> GetEntriesInScope(Guid scopeId)
        => Entries.Where(e => e.ScopeId == scopeId).ToList();

    internal IReadOnlyDictionary<Guid, ActivityInstanceEntry> GetEntriesByIdCache() => EntriesById;

    public void CompleteEntry(Guid activityInstanceId)
    {
        var entry = GetActiveEntry(activityInstanceId);
        entry.Complete();
        ActiveEntryIds.Remove(activityInstanceId);
        _dirtyFlags |= DirtyEntries;
    }

    public void FailEntry(Guid activityInstanceId, int errorCode, string errorMessage)
    {
        var entry = GetActiveEntry(activityInstanceId);
        entry.Fail(errorCode, errorMessage);
        ActiveEntryIds.Remove(activityInstanceId);
        _dirtyFlags |= DirtyEntries;
    }

    public void CancelEntry(Guid activityInstanceId, string reason)
    {
        var entry = GetActiveEntry(activityInstanceId);
        entry.Cancel(reason);
        ActiveEntryIds.Remove(activityInstanceId);
        _dirtyFlags |= DirtyEntries;
    }

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
        _dirtyFlags |= DirtyVariableStates;
    }

    public void StartWith(Guid id, string? processDefinitionId, ActivityInstanceEntry entry, Guid variablesId)
    {
        Initialize(id, processDefinitionId, variablesId);
        Entries.Add(entry);
        _dirtyFlags |= DirtyEntries;

        if (_entriesById != null)
        {
            _entriesById[entry.ActivityInstanceId] = entry;
            _activeEntryIds!.Add(entry.ActivityInstanceId);
        }
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
        ComplexGatewayJoinStates.Clear();
        _dirtyFlags |= DirtyGatewayForks | DirtyComplexGatewayJoinStates;
        CompletedAt = DateTimeOffset.UtcNow;
        IsCompleted = true;
    }

    public void Cancel()
    {
        if (IsCompleted)
            return; // already terminated — cancellation after completion is a no-op

        GatewayForks.Clear();
        ComplexGatewayJoinStates.Clear();
        _dirtyFlags |= DirtyGatewayForks | DirtyComplexGatewayJoinStates;
        CompletedAt = DateTimeOffset.UtcNow;
        IsCompleted = true;
        IsCancelled = true;
    }

    public Guid AddCloneOfVariableState(Guid variableStateId)
    {
        var source = VariableStates.First(v => v.Id == variableStateId);
        var clonedState = new WorkflowVariablesState(Guid.NewGuid(), Id, source.ParentVariablesId);
        clonedState.Merge(source.Variables);

        VariableStates.Add(clonedState);
        _dirtyFlags |= DirtyVariableStates;
        return clonedState.Id;
    }

    public void AddCloneOfVariableState(Guid newScopeId, Guid sourceScopeId)
    {
        var source = VariableStates.First(v => v.Id == sourceScopeId);
        var clonedState = new WorkflowVariablesState(newScopeId, Id, source.ParentVariablesId);
        clonedState.Merge(source.Variables);

        VariableStates.Add(clonedState);
        _dirtyFlags |= DirtyVariableStates;
    }

    public Guid AddChildVariableState(Guid parentVariablesId)
    {
        var childState = new WorkflowVariablesState(Guid.NewGuid(), Id, parentVariablesId);
        VariableStates.Add(childState);
        _dirtyFlags |= DirtyVariableStates;
        return childState.Id;
    }

    public void AddChildVariableState(Guid childId, Guid parentVariablesId)
    {
        var childState = new WorkflowVariablesState(childId, Id, parentVariablesId);
        VariableStates.Add(childState);
        _dirtyFlags |= DirtyVariableStates;
    }

    public void AddConditionSequenceStates(Guid activityInstanceId, string[] sequenceFlowIds)
    {
        var sequenceStates = sequenceFlowIds.Select(id =>
            new ConditionSequenceState(id, activityInstanceId, Id));
        ConditionSequenceStates.AddRange(sequenceStates);
        _dirtyFlags |= DirtyConditionSequenceStates;
    }

    public void CompleteEntries(List<ActivityInstanceEntry> entries)
    {
        foreach (var entry in entries)
            CompleteEntry(entry.ActivityInstanceId);
    }

    public void CompleteEntry(Guid activityInstanceId, ExpandoObject variables, Guid variablesId)
    {
        var entry = GetActiveEntry(activityInstanceId);
        entry.Complete();
        ActiveEntryIds.Remove(activityInstanceId);
        MergeState(variablesId, variables);
        _dirtyFlags |= DirtyEntries;
    }

    public void ExecuteEntry(Guid activityInstanceId)
    {
        GetActiveEntry(activityInstanceId).Execute();
        _dirtyFlags |= DirtyEntries;
    }

    public void ResetEntryExecuting(Guid activityInstanceId)
    {
        GetActiveEntry(activityInstanceId).ResetExecuting();
        _dirtyFlags |= DirtyEntries;
    }

    public void SetEntryMultiInstanceTotal(Guid activityInstanceId, int total)
    {
        GetActiveEntry(activityInstanceId).SetMultiInstanceTotal(total);
        _dirtyFlags |= DirtyEntries;
    }

    public void SetEntryChildWorkflowInstanceId(Guid activityInstanceId, Guid childWorkflowInstanceId)
    {
        GetActiveEntry(activityInstanceId).SetChildWorkflowInstanceId(childWorkflowInstanceId);
        _dirtyFlags |= DirtyEntries;
    }

    public void AddEntries(IEnumerable<ActivityInstanceEntry> entries)
    {
        if (_entriesById != null)
        {
            var entriesList = entries.ToList();
            Entries.AddRange(entriesList);
            foreach (var entry in entriesList)
            {
                _entriesById[entry.ActivityInstanceId] = entry;
                _activeEntryIds!.Add(entry.ActivityInstanceId);
            }
        }
        else
        {
            Entries.AddRange(entries);
        }
        _dirtyFlags |= DirtyEntries;
    }

    public void SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        var sequence = ConditionSequenceStates
            .FirstOrDefault(c => c.GatewayActivityInstanceId == activityInstanceId
                && c.ConditionalSequenceFlowId == sequenceId)
            ?? throw new InvalidOperationException("Sequence not found");

        sequence.SetResult(result);
        _dirtyFlags |= DirtyConditionSequenceStates;
    }

    public void MergeState(Guid variablesId, ExpandoObject variables)
    {
        GetVariableState(variablesId).Merge(variables);
        _dirtyFlags |= DirtyVariableStates;
    }

    public void RemoveVariableStates(IEnumerable<Guid> variableStateIds)
    {
        var idsToRemove = new HashSet<Guid>(variableStateIds);
        VariableStates.RemoveAll(vs => idsToRemove.Contains(vs.Id));
        _dirtyFlags |= DirtyVariableStates;
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
        _dirtyFlags |= DirtyGatewayForks;
        return fork;
    }

    public GatewayForkState? FindGatewayFork(Guid forkInstanceId)
        => GatewayForks.FirstOrDefault(f => f.ForkInstanceId == forkInstanceId);

    public GatewayForkState GetGatewayFork(Guid forkInstanceId)
        => GatewayForks.First(f => f.ForkInstanceId == forkInstanceId);

    public void AddTokenToFork(Guid forkInstanceId, Guid tokenId)
    {
        var fork = GetGatewayFork(forkInstanceId);
        fork.CreatedTokenIds.Add(tokenId);
        _dirtyFlags |= DirtyGatewayForks;
    }

    public GatewayForkState? FindForkByToken(Guid tokenId)
        => GatewayForks.FirstOrDefault(f => f.CreatedTokenIds.Contains(tokenId));

    public void RemoveGatewayFork(Guid forkInstanceId)
    {
        GatewayForks.RemoveAll(f => f.ForkInstanceId == forkInstanceId);
        _dirtyFlags |= DirtyGatewayForks;
    }

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
            {
                TimerCycleTracking.Remove(existing);
                _dirtyFlags |= DirtyTimerCycleTracking;
            }
        }
        else if (existing is not null)
        {
            existing.UpdateFrom(definition);
            _dirtyFlags |= DirtyTimerCycleTracking;
        }
        else
        {
            TimerCycleTracking.Add(new TimerCycleTrackingState(hostActivityInstanceId, timerActivityId, definition, Id));
            _dirtyFlags |= DirtyTimerCycleTracking;
        }
    }

    public ComplexGatewayJoinState? GetComplexGatewayJoinState(string gatewayActivityId)
        => ComplexGatewayJoinStates.FirstOrDefault(s => s.GatewayActivityId == gatewayActivityId);

    public void CreateComplexGatewayJoinState(string gatewayActivityId, Guid firstActivityInstanceId, string activationCondition, Guid workflowInstanceId)
    {
        var newState = new ComplexGatewayJoinState(gatewayActivityId, firstActivityInstanceId, activationCondition, workflowInstanceId);
        ComplexGatewayJoinStates.Add(newState);
        _dirtyFlags |= DirtyComplexGatewayJoinStates;
    }

    public void IncrementComplexGatewayTokenCount(string gatewayActivityId)
    {
        var state = GetComplexGatewayJoinState(gatewayActivityId)
            ?? throw new InvalidOperationException($"ComplexGatewayJoinState not found for gateway '{gatewayActivityId}'");
        state.IncrementTokenCount();
        _dirtyFlags |= DirtyComplexGatewayJoinStates;
    }

    public void MarkComplexGatewayJoinFired(string gatewayActivityId)
    {
        var state = GetComplexGatewayJoinState(gatewayActivityId)
            ?? throw new InvalidOperationException($"ComplexGatewayJoinState not found for gateway '{gatewayActivityId}'");
        state.MarkFired();
        _dirtyFlags |= DirtyComplexGatewayJoinStates;
    }

    public void RemoveComplexGatewayJoinState(string gatewayActivityId)
    {
        ComplexGatewayJoinStates.RemoveAll(s => s.GatewayActivityId == gatewayActivityId);
        _dirtyFlags |= DirtyComplexGatewayJoinStates;
    }

    public void AddConditionalWatcher(Guid activityInstanceId, string activityId,
        string conditionExpression, Guid variablesId)
    {
        ConditionalWatchers.Add(new ConditionalEventWatcherState
        {
            ActivityInstanceId = activityInstanceId,
            ActivityId = activityId,
            ConditionExpression = conditionExpression,
            VariablesId = variablesId,
            LastEvaluatedResult = false
        });
        _dirtyFlags |= DirtyConditionalWatchers;
    }

    public void RemoveConditionalWatcher(Guid activityInstanceId)
    {
        ConditionalWatchers.RemoveAll(w => w.ActivityInstanceId == activityInstanceId);
        _dirtyFlags |= DirtyConditionalWatchers;
    }

    public void UpdateConditionalWatcherResult(Guid activityInstanceId, bool result)
    {
        var watcher = ConditionalWatchers.FirstOrDefault(w => w.ActivityInstanceId == activityInstanceId);
        if (watcher != null)
        {
            watcher.LastEvaluatedResult = result;
            _dirtyFlags |= DirtyConditionalWatchers;
        }
    }

    // ── Compensation ────────────────────────────────────────────────────────────────

    public void AddCompensationSnapshot(CompletedActivitySnapshot snapshot)
    {
        CompensationLog.Add(snapshot);
        NextCompensationSequence++;
        _dirtyFlags |= DirtyCompensationLog;
    }

    public void MarkSnapshotCompensated(string activityDefinitionId, Guid? scopeId)
    {
        var snapshot = CompensationLog
            .FirstOrDefault(s => s.ActivityDefinitionId == activityDefinitionId
                              && s.ScopeId == scopeId
                              && !s.IsCompensated);
        snapshot?.MarkCompensated();
        _dirtyFlags |= DirtyCompensationLog;
    }

    public void StartCompensationWalk(CompensationWalkState walk)
    {
        ActiveCompensationWalk = walk;
        _dirtyFlags |= DirtyCompensationLog;
    }

    public void ClearCompensationWalk()
    {
        ActiveCompensationWalk = null;
        _dirtyFlags |= DirtyCompensationLog;
    }

    public void SetCompensationHandlerInstanceId(Guid handlerInstanceId)
    {
        ActiveCompensationWalk?.SetCurrentHandler(handlerInstanceId);
        _dirtyFlags |= DirtyCompensationLog;
    }

    public void MarkCurrentHandlerEntry(Guid entryInstanceId)
    {
        var entry = GetActiveEntry(entryInstanceId);
        entry.MarkAsCompensationHandler();
        _dirtyFlags |= DirtyEntries;
    }

    /// <summary>
    /// Returns true if the given activity instance is an active compensation handler entry.
    /// Used by the walk advancement logic to detect completion of the current handler
    /// without querying the full scope-completion check.
    /// </summary>
    public bool HasActiveCompensationHandler(Guid handlerInstanceId)
        => ActiveEntryIds.Contains(handlerInstanceId);
}
