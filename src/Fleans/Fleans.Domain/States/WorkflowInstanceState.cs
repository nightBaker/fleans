using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class WorkflowInstanceState
{
    [Id(0)]
    public Guid Id { get; private set; }

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

    /// <summary>
    /// True when the instance terminated because an unhandled activity failure left it with
    /// no live tokens (#762). Like <see cref="IsCancelled"/>, a failed instance is also
    /// <see cref="IsCompleted"/> (terminal) — check this flag to tell success from failure.
    /// </summary>
    [Id(28)]
    public bool IsFailed { get; private set; }

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

    /// <summary>
    /// Optional caller-supplied business identifier (an order number, a contract id) set at
    /// start and immutable afterwards (#824). Not inherited by call-activity children.
    /// </summary>
    [Id(29)]
    public string? BusinessId { get; private set; }

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

    [Id(17)]
    public List<ComplexGatewayJoinState> ComplexGatewayJoinStates { get; private set; } = [];

    // Transaction Sub-Process outcome tracking. In-memory — excluded from EF Core schema
    // (see FleanModelConfiguration). Populated from grain snapshot if present, otherwise
    // rebuilt from TransactionOutcomeSet events on activation.
    [Id(18)]
    public Dictionary<Guid, TransactionOutcomeRecord> TransactionOutcomes { get; private set; } = new();

    [Id(23)]
    public List<ConditionalEventWatcherState> ConditionalWatchers { get; private set; } = [];

    /// <summary>
    /// Maps each open SubProcess (and Transaction) host's ActivityInstanceId →
    /// its own execution-scope variables id. Populated by the
    /// <c>ScopeHostExecutionScopeOpened</c> projection; pruned by
    /// <c>ApplyActivityCompleted</c>/<c>ApplyActivityCancelled</c> for SubProcess
    /// hosts. In-memory — excluded from EF Core schema; rebuilt from the event
    /// log on activation, like <see cref="TransactionOutcomes"/>.
    /// </summary>
    [Id(24)]
    public Dictionary<Guid, Guid> ScopeHostExecutionScopes { get; private set; } = new();

    /// <summary>
    /// Append-only log of completed, compensable activities (those with a CompensationBoundaryEvent attached).
    /// Keyed implicitly by ScopeId on each entry. Used as input to compensation walks.
    /// </summary>
    [Id(20)]
    public List<CompletedActivitySnapshot> CompensationLog { get; private set; } = [];

    /// <summary>Global monotonic counter for assigning CompletedAtSequence to compensation snapshots.</summary>
    [Id(21)]
    public int NextCompensationSequence { get; private set; }

    /// <summary>
    /// Active compensation walks keyed by scope id. <see cref="Guid.Empty"/> is the root-scope sentinel.
    /// Rebuilt from <c>CompensationWalkStarted</c> / <c>CompensationWalkCompleted</c> events on replay.
    /// Preserved (not removed) on <c>CompensationWalkFailed</c> for observability.
    /// </summary>
    [Id(25)]
    public Dictionary<Guid, CompensationWalkState> ActiveCompensationWalks { get; private set; } = new();

    /// <summary>
    /// Pending external events (child completed/failed/escalation, signal delivery) that have been
    /// enqueued via <c>PendingEventEnqueued</c> but not yet drained. Keyed by op-key. Rebuilt from the
    /// event log on activation (like <see cref="TransactionOutcomes"/>); the in-memory queue's fresh
    /// TaskCompletionSources are minted from this on reactivation. Issue #657.
    /// </summary>
    [Id(26)]
    public Dictionary<string, PendingExternalEventPayload> PendingOperations { get; private set; } = new();

    /// <summary>
    /// Dedup ledger for caller-retried operations: op-key → durable outcome (null for void ops).
    /// Populated only by the three retried methods (child completed/failed/escalation) via
    /// <c>PendingEventApplied</c>; consulted on retry to short-circuit double-apply. Cleared on
    /// terminal state (the existing IsCompleted stale guard protects post-completion retries). Issue #657.
    /// </summary>
    [Id(27)]
    public Dictionary<string, PendingEventResult?> AppliedOperations { get; private set; } = new();

    // ── Pending external events (issue #657) ────────────────────────────────────────

    public void AddPendingOperation(string opKey, PendingExternalEventPayload payload)
    {
        PendingOperations[opKey] = payload;
    }

    public void RemovePendingOperation(string opKey)
    {
        PendingOperations.Remove(opKey);
    }

    public void RecordAppliedOperation(string opKey, PendingEventResult? result)
    {
        AppliedOperations[opKey] = result;
    }

    public void ClearAppliedOperations()
    {
        AppliedOperations.Clear();
    }

    public bool TryGetAppliedOperation(string opKey, out PendingEventResult? result)
        => AppliedOperations.TryGetValue(opKey, out result);

    public bool HasPendingOperation(string opKey) => PendingOperations.ContainsKey(opKey);

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

    public void CompleteEntry(Guid activityInstanceId, DateTimeOffset at)
    {
        var entry = GetActiveEntry(activityInstanceId);
        entry.Complete(at);
        ActiveEntryIds.Remove(activityInstanceId);
    }

    public void FailEntry(Guid activityInstanceId, string errorCode, string errorMessage, DateTimeOffset at)
    {
        var entry = GetActiveEntry(activityInstanceId);
        entry.Fail(errorCode, errorMessage, at);
        ActiveEntryIds.Remove(activityInstanceId);
    }

    public void CancelEntry(Guid activityInstanceId, string reason, DateTimeOffset at)
    {
        var entry = GetActiveEntry(activityInstanceId);
        entry.Cancel(reason, at);
        ActiveEntryIds.Remove(activityInstanceId);
    }

    public Guid GetRootVariablesId()
        => VariableStates.First().Id;

    public WorkflowVariablesState GetVariableState(Guid id)
        => VariableStates.FirstOrDefault(v => v.Id == id)
            ?? throw new InvalidOperationException($"Variable state '{id}' not found");

    public IEnumerable<ConditionSequenceState> GetConditionSequenceStatesForGateway(Guid gatewayActivityInstanceId)
        => ConditionSequenceStates.Where(c => c.GatewayActivityInstanceId == gatewayActivityInstanceId);

    public void Initialize(Guid id, string? processDefinitionId, Guid variablesId, DateTimeOffset createdAt, string? businessId = null)
    {
        Id = id;
        ProcessDefinitionId = processDefinitionId;
        BusinessId = businessId;
        CreatedAt = createdAt;
        VariableStates.Add(new WorkflowVariablesState(variablesId, id));
    }

    public void SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId)
    {
        ParentWorkflowInstanceId = parentWorkflowInstanceId;
        ParentActivityId = parentActivityId;
    }

    public void Start(DateTimeOffset at)
    {
        if (IsStarted)
            throw new InvalidOperationException("Workflow is already started");
        
        ExecutionStartedAt = at;
        IsStarted = true;
    }

    public void Complete(DateTimeOffset at)
    {
        if (IsCompleted)
            throw new InvalidOperationException("Workflow is already completed");

        GatewayForks.Clear();
        ComplexGatewayJoinStates.Clear();
        AppliedOperations.Clear(); // dedup ledger GC: bounded to instance lifetime (#657, Q1')
        CompletedAt = at;
        IsCompleted = true;
    }

    public void Cancel(DateTimeOffset at)
    {
        if (IsCompleted)
            return; // already terminated — cancellation after completion is a no-op

        GatewayForks.Clear();
        ComplexGatewayJoinStates.Clear();
        AppliedOperations.Clear(); // dedup ledger GC: bounded to instance lifetime (#657, Q1')
        CompletedAt = at;
        IsCompleted = true;
        IsCancelled = true;
    }

    /// <summary>
    /// Terminal failure: the instance can make no further progress because an unhandled
    /// activity failure consumed its last token. Mirrors <see cref="Cancel"/> — marks the
    /// instance terminal (IsCompleted + CompletedAt) and sets <see cref="IsFailed"/>.
    /// </summary>
    public void Fail(DateTimeOffset at)
    {
        if (IsCompleted)
            return; // already terminated — a late failure after termination is a no-op

        GatewayForks.Clear();
        ComplexGatewayJoinStates.Clear();
        AppliedOperations.Clear(); // dedup ledger GC: bounded to instance lifetime (#657, Q1')
        CompletedAt = at;
        IsCompleted = true;
        IsFailed = true;
    }

    public void AddCloneOfVariableState(Guid newScopeId, Guid sourceScopeId)
    {
        var source = VariableStates.First(v => v.Id == sourceScopeId);
        var clonedState = new WorkflowVariablesState(newScopeId, Id, source.ParentVariablesId);
        clonedState.Merge(source.Variables);

        VariableStates.Add(clonedState);
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

    public void CompleteEntry(Guid activityInstanceId, ExpandoObject variables, Guid variablesId, DateTimeOffset at)
    {
        CompleteEntry(activityInstanceId, at);
        MergeState(variablesId, variables);
    }

    public void ExecuteEntry(Guid activityInstanceId, DateTimeOffset at)
    {
        GetActiveEntry(activityInstanceId).Execute(at);
    }

    public void ResetEntryExecuting(Guid activityInstanceId)
    {
        GetActiveEntry(activityInstanceId).ResetExecuting();
    }

    public void SetEntryMultiInstanceTotal(Guid activityInstanceId, int total)
    {
        GetActiveEntry(activityInstanceId).SetMultiInstanceTotal(total);
    }

    public void SetEntryChildWorkflowInstanceId(Guid activityInstanceId, Guid childWorkflowInstanceId)
    {
        GetActiveEntry(activityInstanceId).SetChildWorkflowInstanceId(childWorkflowInstanceId);
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

    public void AddTokenToFork(Guid forkInstanceId, Guid tokenId)
    {
        var fork = GetGatewayFork(forkInstanceId);
        fork.CreatedTokenIds.Add(tokenId);
    }

    public GatewayForkState? FindForkByToken(Guid tokenId)
        => GatewayForks.FirstOrDefault(f => f.CreatedTokenIds.Contains(tokenId));

    public void RemoveGatewayFork(Guid forkInstanceId)
    {
        GatewayForks.RemoveAll(f => f.ForkInstanceId == forkInstanceId);
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
            }
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

    public ComplexGatewayJoinState? GetComplexGatewayJoinState(string gatewayActivityId)
        => ComplexGatewayJoinStates.FirstOrDefault(s => s.GatewayActivityId == gatewayActivityId);

    public void CreateComplexGatewayJoinState(string gatewayActivityId, Guid firstActivityInstanceId, string activationCondition, Guid workflowInstanceId)
    {
        var newState = new ComplexGatewayJoinState(gatewayActivityId, firstActivityInstanceId, activationCondition, workflowInstanceId);
        ComplexGatewayJoinStates.Add(newState);
    }

    public void IncrementComplexGatewayTokenCount(string gatewayActivityId)
    {
        var state = GetComplexGatewayJoinState(gatewayActivityId)
            ?? throw new InvalidOperationException($"ComplexGatewayJoinState not found for gateway '{gatewayActivityId}'");
        state.IncrementTokenCount();
    }

    public void MarkComplexGatewayJoinFired(string gatewayActivityId)
    {
        var state = GetComplexGatewayJoinState(gatewayActivityId)
            ?? throw new InvalidOperationException($"ComplexGatewayJoinState not found for gateway '{gatewayActivityId}'");
        state.MarkFired();
    }

    public void RemoveComplexGatewayJoinState(string gatewayActivityId)
    {
        ComplexGatewayJoinStates.RemoveAll(s => s.GatewayActivityId == gatewayActivityId);
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
    }

    public void RemoveConditionalWatcher(Guid activityInstanceId)
    {
        ConditionalWatchers.RemoveAll(w => w.ActivityInstanceId == activityInstanceId);
    }

    public void UpdateConditionalWatcherResult(Guid activityInstanceId, bool result)
    {
        var watcher = ConditionalWatchers.FirstOrDefault(w => w.ActivityInstanceId == activityInstanceId);
        if (watcher != null)
        {
            watcher.LastEvaluatedResult = result;
        }
    }

    // ── Compensation ────────────────────────────────────────────────────────────────

    public void AddCompensationSnapshot(CompletedActivitySnapshot snapshot)
    {
        CompensationLog.Add(snapshot);
        NextCompensationSequence++;
    }

    public void MarkSnapshotCompensated(string activityDefinitionId, Guid? scopeId)
    {
        var snapshot = CompensationLog
            .FirstOrDefault(s => s.ActivityDefinitionId == activityDefinitionId
                              && s.ScopeId == scopeId
                              && !s.IsCompensated);
        snapshot?.MarkCompensated();
    }

    public void StartCompensationWalk(Guid? scopeId, CompensationWalkState walk)
    {
        ActiveCompensationWalks[scopeId ?? Guid.Empty] = walk;
    }

    public void ClearCompensationWalk(Guid? scopeId)
    {
        ActiveCompensationWalks.Remove(scopeId ?? Guid.Empty);
    }

    public CompensationWalkState? GetCompensationWalk(Guid? scopeId)
        => ActiveCompensationWalks.TryGetValue(scopeId ?? Guid.Empty, out var w) ? w : null;

    /// <summary>
    /// Returns the walk that has an active handler, or null if none.
    /// At most one walk has a non-null <c>CurrentHandlerInstanceId</c> at any time.
    /// </summary>
    public CompensationWalkState? GetActiveWalkWithHandler()
        => ActiveCompensationWalks.Values.FirstOrDefault(w => w.CurrentHandlerInstanceId != null);

    public void SetCompensationHandlerInstanceId(Guid? scopeId, Guid handlerInstanceId)
    {
        GetCompensationWalk(scopeId)?.SetCurrentHandler(handlerInstanceId);
    }

    public void MarkCurrentHandlerEntry(Guid entryInstanceId)
    {
        var entry = GetActiveEntry(entryInstanceId);
        entry.MarkAsCompensationHandler();
    }

    /// <summary>
    /// Returns true if the given activity instance is an active compensation handler entry.
    /// Used by the walk advancement logic to detect completion of the current handler
    /// without querying the full scope-completion check.
    /// </summary>
    public bool HasActiveCompensationHandler(Guid handlerInstanceId)
        => ActiveEntryIds.Contains(handlerInstanceId);
}
