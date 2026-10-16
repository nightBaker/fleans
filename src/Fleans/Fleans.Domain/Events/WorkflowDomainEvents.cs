using System.Dynamic;

namespace Fleans.Domain.Events;

/// <summary>
/// Base of every event in the <c>WorkflowInstance</c> journal. Events are immutable facts: an
/// <c>Apply</c> handler reads only the event and the current state — never the clock, never
/// <c>Guid.NewGuid()</c> — so replaying the journal rebuilds exactly the state the live run had.
/// </summary>
/// <remarks>
/// <see cref="OccurredAt"/> is stamped by <c>WorkflowExecution.Emit</c>; event sites never set it.
/// Handlers that record a time (<c>CreatedAt</c>, <c>CompletedAt</c>, …) take it from here.
/// </remarks>
[GenerateSerializer]
public abstract record WorkflowEvent : IDomainEvent
{
    [Id(0)]
    public DateTimeOffset OccurredAt { get; init; }
}

// Workflow lifecycle
// BusinessId (#824): optional caller-supplied business identifier, immutable for the
// instance's lifetime; null when the caller supplied none.
public record WorkflowStarted(Guid InstanceId, string? ProcessDefinitionId, Guid RootVariablesId,
    string? BusinessId = null) : WorkflowEvent;
public record ExecutionStarted : WorkflowEvent;
public record WorkflowCompleted : WorkflowEvent;
public record WorkflowCancelled(string Reason) : WorkflowEvent;
/// <summary>
/// The instance terminated because an unhandled activity failure left it with no live
/// tokens (no active activities, no completed root end event). Carries the failure that
/// ended it so the terminal state is self-describing (#762).
/// </summary>
public record WorkflowFailed(
    string FailedActivityId, Guid FailedActivityInstanceId, string ErrorCode, string ErrorMessage) : WorkflowEvent;

// Activity lifecycle
public record ActivitySpawned(
    Guid ActivityInstanceId, string ActivityId, string ActivityType,
    Guid VariablesId, Guid? ScopeId, int? MultiInstanceIndex,
    Guid? TokenId) : WorkflowEvent;
public record ActivityExecutionStarted(Guid ActivityInstanceId) : WorkflowEvent;
public record ActivityCompleted(
    Guid ActivityInstanceId, Guid VariablesId, ExpandoObject Variables) : WorkflowEvent;
public record ActivityFailed(
    Guid ActivityInstanceId, string ErrorCode, string ErrorMessage) : WorkflowEvent;
public record ActivityExecutionReset(Guid ActivityInstanceId) : WorkflowEvent;
public record ActivityCancelled(Guid ActivityInstanceId, string Reason) : WorkflowEvent;
public record MultiInstanceTotalSet(Guid ActivityInstanceId, int Total) : WorkflowEvent;

// Variable management
public record VariablesMerged(Guid VariablesId, ExpandoObject Variables) : WorkflowEvent;
public record ChildVariableScopeCreated(Guid ScopeId, Guid ParentScopeId) : WorkflowEvent;
public record VariableScopeCloned(Guid NewScopeId, Guid SourceScopeId) : WorkflowEvent;
public record VariableScopesRemoved(IReadOnlyList<Guid> ScopeIds) : WorkflowEvent;

/// <summary>
/// Tracks the binding between a scope-host activity instance (SubProcess or
/// Transaction) and its own execution-scope variables id, so that scope
/// completion can disambiguate the host's own scope from sibling
/// EventSubProcess handler scopes that share the host's parent variables id.
/// Emitted from <c>ProcessOpenSubProcess</c>; cleanup is folded into
/// <c>ApplyActivityCompleted</c>/<c>ApplyActivityCancelled</c>.
/// </summary>
public record ScopeHostExecutionScopeOpened(Guid HostInstanceId, Guid ExecutionScopeId) : WorkflowEvent;

// Gateway/token management
public record ConditionSequencesAdded(
    Guid GatewayInstanceId, string[] SequenceFlowIds) : WorkflowEvent;
public record ConditionSequenceEvaluated(
    Guid GatewayInstanceId, string SequenceFlowId, bool Result) : WorkflowEvent;
public record GatewayForkCreated(Guid ForkInstanceId, Guid? ConsumedTokenId) : WorkflowEvent;
public record GatewayForkTokenAdded(Guid ForkInstanceId, Guid TokenId) : WorkflowEvent;
public record GatewayForkRemoved(Guid ForkInstanceId) : WorkflowEvent;
public record ComplexGatewayJoinStateCreated(string GatewayActivityId, Guid FirstActivityInstanceId, string ActivationCondition, Guid WorkflowInstanceId) : WorkflowEvent;
public record ComplexGatewayJoinStateTokenIncremented(string GatewayActivityId) : WorkflowEvent;
public record ComplexGatewayJoinStateFired(string GatewayActivityId) : WorkflowEvent;
public record ComplexGatewayJoinStateRemoved(string GatewayActivityId) : WorkflowEvent;

// Parent/child
public record ParentInfoSet(Guid ParentInstanceId, string ParentActivityId) : WorkflowEvent;
public record ChildWorkflowLinked(Guid ActivityInstanceId, Guid ChildWorkflowInstanceId) : WorkflowEvent;

// User task lifecycle
public record UserTaskRegistered(
    Guid ActivityInstanceId, string? Assignee,
    IReadOnlyList<string> CandidateGroups, IReadOnlyList<string> CandidateUsers,
    IReadOnlyList<string>? ExpectedOutputVariables) : WorkflowEvent;
public record UserTaskClaimed(Guid ActivityInstanceId, string UserId) : WorkflowEvent;
public record UserTaskUnclaimed(Guid ActivityInstanceId) : WorkflowEvent;
public record UserTaskUnregistered(Guid ActivityInstanceId) : WorkflowEvent;
public record UserTaskFailed(Guid ActivityInstanceId, string ActivityId, string ErrorCode, string ErrorMessage) : WorkflowEvent;
public record UserTaskCancelled(Guid ActivityInstanceId, string ActivityId, string? Reason) : WorkflowEvent;

// Escalation
public record EscalationUncaughtRaised(
    string EscalationCode, string SourceActivityId) : WorkflowEvent;

// Timer cycle tracking
public record TimerCycleUpdated(
    Guid HostActivityInstanceId, string TimerActivityId,
    Activities.TimerDefinition? RemainingCycle) : WorkflowEvent;

// Conditional event watchers
public record ConditionalWatcherRegistered(
    Guid ActivityInstanceId, string ActivityId, string ConditionExpression,
    Guid VariablesId) : WorkflowEvent;
public record ConditionalWatcherFired(Guid ActivityInstanceId) : WorkflowEvent;
public record ConditionalWatcherCleared(Guid ActivityInstanceId) : WorkflowEvent;
public record ConditionalWatcherResultUpdated(Guid ActivityInstanceId, bool Result) : WorkflowEvent;

// Compensation
public record CompensableActivitySnapshotRecorded(
    Guid ActivityInstanceId,
    string ActivityDefinitionId,
    ExpandoObject VariablesSnapshot,
    Guid? ScopeId) : WorkflowEvent;

public record CompensationWalkStarted(
    Guid? ScopeId,
    string? TargetActivityRef,
    int HandlerCount,
    Guid ThrowerActivityInstanceId) : WorkflowEvent;

public record CompensationHandlerSpawned(
    Guid HandlerInstanceId,
    string CompensableActivityDefinitionId,
    string HandlerActivityId,
    Guid? ScopeId) : WorkflowEvent;

public record CompensationEntryMarkedCompensated(
    string ActivityDefinitionId,
    Guid? ScopeId) : WorkflowEvent;

public record CompensationWalkCompleted(Guid? ScopeId) : WorkflowEvent;

public record CompensationWalkFailed(
    Guid? ScopeId,
    Guid HandlerInstanceId,
    string ErrorCode,
    string ErrorMessage) : WorkflowEvent;

public record CompensationWalkAborted(
    Guid? ScopeId,
    string Reason) : WorkflowEvent;

// Transaction Sub-Process outcome
// Plain record — no [GenerateSerializer] — stored via Newtonsoft.Json in EfCoreEventStore,
// consistent with all other JournaledGrain events in this file.
public record TransactionOutcomeSet(
    Guid TransactionInstanceId,
    States.TransactionOutcome Outcome,
    string? ErrorCode,
    string? ErrorMessage) : WorkflowEvent;
