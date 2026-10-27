using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Newtonsoft.Json;

namespace Fleans.Application.QueryModels;

/// <summary>
/// One event of an instance's journal, as the admin UI's debug mode lists it.
/// </summary>
/// <param name="Version">The journal version the event was stored at (1 = the first event).</param>
/// <param name="EventType">The event's type name, e.g. <c>ActivityCompleted</c>.</param>
/// <param name="Summary">One line describing what the event did.</param>
/// <param name="Kind">How the UI colours it: <c>info</c>, <c>ok</c>, <c>error</c> or <c>warning</c>.</param>
/// <param name="ActivityInstanceId">The activity instance the event is about, if any.</param>
/// <param name="ActivityId">That activity instance's BPMN element id.</param>
/// <param name="VariableChanges">The variable values the event added, changed or removed.</param>
public sealed record JournalStep(
    int Version, DateTimeOffset OccurredAt, string EventType, string Summary, string Kind,
    Guid? ActivityInstanceId, string? ActivityId, List<VariableChange> VariableChanges);

/// <summary>
/// One variable value an event changed. <see cref="Before"/> is null for a value it added,
/// <see cref="After"/> null for one it removed (its scope was removed).
/// </summary>
public sealed record VariableChange(Guid VariablesId, string Name, string? Before, string? After);

/// <summary>
/// An instance's whole journal, replayable step by step — the admin UI's debug mode. The state
/// after any version is rebuilt by replaying the events up to it, exactly as the grain rebuilds
/// itself on activation (<c>Apply</c> handlers read only the event), so each step shows the state
/// the live run had at that point.
/// </summary>
public sealed class InstanceJournal
{
    private readonly IReadOnlyList<WorkflowEvent> _events;

    /// <summary>One step per journal event, oldest first; step <c>i</c> has version <c>i + 1</c>.</summary>
    public IReadOnlyList<JournalStep> Steps { get; }

    internal InstanceJournal(IReadOnlyList<WorkflowEvent> events)
    {
        _events = events;
        Steps = BuildSteps(events);
    }

    /// <summary>
    /// Reads the instance's whole journal. Throws <see cref="InvalidOperationException"/> when it holds
    /// more events than the store loads at once (<c>Persistence:MaxEventsPerLoad</c>).
    /// </summary>
    public static async Task<InstanceJournal> LoadAsync(IEventStore eventStore, Guid instanceId)
        => new(await eventStore.ReadEventsAsync(instanceId, 0));

    /// <summary>The instance's state after the first <paramref name="version"/> events (0 = before any).</summary>
    public InstanceStateSnapshot StateAt(int version)
        => InstanceStateSnapshots.From(Replay(version), definition: null, version, isFromReadModel: false);

    /// <summary>The event stored at <paramref name="version"/>, as indented JSON.</summary>
    public string PayloadAt(int version)
        => JsonConvert.SerializeObject(_events[version - 1], Formatting.Indented);

    private WorkflowInstanceState Replay(int version)
    {
        var state = new WorkflowInstanceState();
        var replay = new WorkflowExecution(state);
        foreach (var evt in _events.Take(Math.Clamp(version, 0, _events.Count)))
            replay.ReplayEvent(evt);
        return state;
    }

    private static List<JournalStep> BuildSteps(IReadOnlyList<WorkflowEvent> events)
    {
        var steps = new List<JournalStep>(events.Count);
        var state = new WorkflowInstanceState();
        var replay = new WorkflowExecution(state);
        var before = CaptureVariables(state);

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            replay.ReplayEvent(evt);

            var after = CaptureVariables(state);
            var changes = DiffVariables(before, after);
            before = after;

            var activityInstanceId = ActivityInstanceIdOf(evt);
            var activityId = evt is ActivitySpawned spawned
                ? spawned.ActivityId
                : activityInstanceId is { } id ? state.Entries.FirstOrDefault(e => e.ActivityInstanceId == id)?.ActivityId : null;

            var (summary, kind) = Describe(evt, activityId ?? ShortId(activityInstanceId), changes.Count);
            steps.Add(new JournalStep(i + 1, evt.OccurredAt, evt.GetType().Name, summary, kind,
                activityInstanceId, activityId, changes));
        }

        return steps;
    }

    /// <summary>Every scope's values, formatted as the instance page shows them.</summary>
    private static Dictionary<Guid, Dictionary<string, string>> CaptureVariables(WorkflowInstanceState state)
        => state.VariableStates.ToDictionary(vs => vs.Id, vs =>
        {
            // The same JSON round trip as InstanceStateSnapshots, so a value reads the same here as in StateAt.
            var stored = JsonConvert.DeserializeObject<ExpandoObject>(JsonConvert.SerializeObject(vs.Variables))
                ?? new ExpandoObject();
            return ((IDictionary<string, object?>)stored)
                .ToDictionary(e => e.Key, e => VariableValueFormatter.Format(e.Value));
        });

    private static List<VariableChange> DiffVariables(
        Dictionary<Guid, Dictionary<string, string>> before, Dictionary<Guid, Dictionary<string, string>> after)
    {
        var changes = new List<VariableChange>();
        foreach (var (scopeId, values) in after)
        {
            var old = before.GetValueOrDefault(scopeId);
            foreach (var (name, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                if (old is null || !old.TryGetValue(name, out var oldValue))
                    changes.Add(new VariableChange(scopeId, name, null, value));
                else if (oldValue != value)
                    changes.Add(new VariableChange(scopeId, name, oldValue, value));
            }
        }

        foreach (var (scopeId, values) in before)
        {
            var current = after.GetValueOrDefault(scopeId);
            foreach (var (name, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                if (current is null || !current.ContainsKey(name))
                    changes.Add(new VariableChange(scopeId, name, value, null));
            }
        }

        return changes;
    }

    private static Guid? ActivityInstanceIdOf(WorkflowEvent evt) => evt switch
    {
        WorkflowFailed e => e.FailedActivityInstanceId,
        ActivitySpawned e => e.ActivityInstanceId,
        ActivityExecutionStarted e => e.ActivityInstanceId,
        ActivityCompleted e => e.ActivityInstanceId,
        ActivityFailed e => e.ActivityInstanceId,
        ActivityExecutionReset e => e.ActivityInstanceId,
        ActivityCancelled e => e.ActivityInstanceId,
        MultiInstanceTotalSet e => e.ActivityInstanceId,
        ScopeHostExecutionScopeOpened e => e.HostInstanceId,
        ActivityInputScopeOpened e => e.ActivityInstanceId,
        ConditionSequencesAdded e => e.GatewayInstanceId,
        ConditionSequenceEvaluated e => e.GatewayInstanceId,
        GatewayForkCreated e => e.ForkInstanceId,
        GatewayForkTokenAdded e => e.ForkInstanceId,
        GatewayForkRemoved e => e.ForkInstanceId,
        ComplexGatewayJoinStateCreated e => e.FirstActivityInstanceId,
        ChildWorkflowLinked e => e.ActivityInstanceId,
        UserTaskRegistered e => e.ActivityInstanceId,
        UserTaskClaimed e => e.ActivityInstanceId,
        UserTaskUnclaimed e => e.ActivityInstanceId,
        UserTaskUnregistered e => e.ActivityInstanceId,
        UserTaskFailed e => e.ActivityInstanceId,
        UserTaskCancelled e => e.ActivityInstanceId,
        TimerCycleUpdated e => e.HostActivityInstanceId,
        ConditionalWatcherRegistered e => e.ActivityInstanceId,
        ConditionalWatcherFired e => e.ActivityInstanceId,
        ConditionalWatcherCleared e => e.ActivityInstanceId,
        ConditionalWatcherResultUpdated e => e.ActivityInstanceId,
        CompensableActivitySnapshotRecorded e => e.ActivityInstanceId,
        CompensationHandlerSpawned e => e.HandlerInstanceId,
        CompensationWalkStarted e => e.ThrowerActivityInstanceId,
        CompensationWalkFailed e => e.HandlerInstanceId,
        TransactionOutcomeSet e => e.TransactionInstanceId,
        _ => null
    };

    private static (string Summary, string Kind) Describe(WorkflowEvent evt, string? activity, int variableChanges) => evt switch
    {
        WorkflowStarted e => ($"Instance started{(e.BusinessId is null ? "" : $" · business id {e.BusinessId}")}", "info"),
        ExecutionStarted => ("Execution started", "info"),
        WorkflowCompleted => ("Instance completed", "ok"),
        WorkflowCancelled e => ($"Instance cancelled: {e.Reason}", "warning"),
        WorkflowFailed e => ($"Instance failed at {activity}: [{e.ErrorCode}] {e.ErrorMessage}", "error"),
        ActivitySpawned e => ($"{e.ActivityId} spawned ({e.ActivityType}{(e.MultiInstanceIndex is { } mi ? $" #{mi}" : "")})", "info"),
        ActivityExecutionStarted => ($"{activity} started", "info"),
        ActivityCompleted => ($"{activity} completed{Changed(variableChanges)}", "ok"),
        ActivityFailed e => ($"{activity} failed: [{e.ErrorCode}] {e.ErrorMessage}", "error"),
        ActivityExecutionReset => ($"{activity} execution reset", "warning"),
        ActivityCancelled e => ($"{activity} cancelled: {e.Reason}", "warning"),
        MultiInstanceTotalSet e => ($"{activity} runs {e.Total} instances", "info"),
        VariablesMerged e => ($"Variables merged into scope {ShortId(e.VariablesId)}{Changed(variableChanges)}", "info"),
        ChildVariableScopeCreated e => ($"Scope {ShortId(e.ScopeId)} created under {ShortId(e.ParentScopeId)}", "info"),
        VariableScopeCloned e => ($"Scope {ShortId(e.NewScopeId)} cloned from {ShortId(e.SourceScopeId)}", "info"),
        VariableScopesRemoved e => ($"{e.ScopeIds.Count} scope(s) removed", "info"),
        ScopeHostExecutionScopeOpened e => ($"{activity} opened scope {ShortId(e.ExecutionScopeId)}", "info"),
        ActivityInputScopeOpened e => ($"{activity} mapped inputs into scope {ShortId(e.ScopeId)}{Changed(variableChanges)}", "info"),
        ConditionSequencesAdded e => ($"{activity} evaluates {e.SequenceFlowIds.Length} condition(s)", "info"),
        ConditionSequenceEvaluated e => ($"{activity}: {e.SequenceFlowId} = {(e.Result ? "true" : "false")}", e.Result ? "ok" : "info"),
        GatewayForkCreated => ($"{activity} forked", "info"),
        GatewayForkTokenAdded e => ($"{activity} fork token {ShortId(e.TokenId)} added", "info"),
        GatewayForkRemoved => ($"{activity} fork removed", "info"),
        ComplexGatewayJoinStateCreated e => ($"{e.GatewayActivityId} join waits for: {e.ActivationCondition}", "info"),
        ComplexGatewayJoinStateTokenIncremented e => ($"{e.GatewayActivityId} join received a token", "info"),
        ComplexGatewayJoinStateFired e => ($"{e.GatewayActivityId} join fired", "ok"),
        ComplexGatewayJoinStateRemoved e => ($"{e.GatewayActivityId} join removed", "info"),
        ParentInfoSet e => ($"Called by {ShortId(e.ParentInstanceId)} at {e.ParentActivityId}", "info"),
        ChildWorkflowLinked e => ($"{activity} started child instance {ShortId(e.ChildWorkflowInstanceId)}", "info"),
        UserTaskRegistered e => ($"{activity} waits for a user{(e.Assignee is null ? "" : $" · assignee {e.Assignee}")}", "info"),
        UserTaskClaimed e => ($"{activity} claimed by {e.UserId}", "info"),
        UserTaskUnclaimed => ($"{activity} unclaimed", "info"),
        UserTaskUnregistered => ($"{activity} user task closed", "info"),
        UserTaskFailed e => ($"{activity} user task failed: [{e.ErrorCode}] {e.ErrorMessage}", "error"),
        UserTaskCancelled e => ($"{activity} user task cancelled{(e.Reason is null ? "" : $": {e.Reason}")}", "warning"),
        EscalationUncaughtRaised e => ($"Escalation {e.EscalationCode} from {e.SourceActivityId} not caught", "warning"),
        TimerCycleUpdated e => ($"{e.TimerActivityId} timer cycle {(e.RemainingCycle is null ? "finished" : "updated")}", "info"),
        ConditionalWatcherRegistered e => ($"{activity} watches: {e.ConditionExpression}", "info"),
        ConditionalWatcherFired => ($"{activity} condition fired", "ok"),
        ConditionalWatcherCleared => ($"{activity} condition watcher cleared", "info"),
        ConditionalWatcherResultUpdated e => ($"{activity} condition is {(e.Result ? "true" : "false")}", "info"),
        CompensableActivitySnapshotRecorded e => ($"{e.ActivityDefinitionId} recorded for compensation", "info"),
        CompensationWalkStarted e => ($"Compensation started: {e.HandlerCount} handler(s)", "warning"),
        CompensationHandlerSpawned e => ($"{e.HandlerActivityId} compensates {e.CompensableActivityDefinitionId}", "warning"),
        CompensationEntryMarkedCompensated e => ($"{e.ActivityDefinitionId} compensated", "ok"),
        CompensationWalkCompleted => ("Compensation completed", "ok"),
        CompensationWalkFailed e => ($"Compensation failed: [{e.ErrorCode}] {e.ErrorMessage}", "error"),
        CompensationWalkAborted e => ($"Compensation aborted: {e.Reason}", "warning"),
        TransactionOutcomeSet e => ($"{activity} transaction {e.Outcome}", e.ErrorCode is null ? "info" : "error"),
        PendingEventEnqueued e => ($"External event queued: {e.Payload.GetType().Name.Replace("Pending", "").Replace("Payload", "")}", "info"),
        PendingEventDrained => ("External event taken from the queue", "info"),
        PendingEventApplied => ("External event applied", "info"),
        _ => (evt.GetType().Name, "info")
    };

    private static string Changed(int variableChanges) => variableChanges switch
    {
        0 => "",
        1 => " · 1 variable changed",
        _ => $" · {variableChanges} variables changed"
    };

    private static string? ShortId(Guid? id) => id?.ToString()[..8];
}
