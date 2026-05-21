using System.Dynamic;

namespace Fleans.Domain.Events;

// Workflow lifecycle
// OccurredAt: emission-time wall clock (UtcNow at emit). Stored in the journal so replays
// see a fixed timestamp instead of recomputing UtcNow inside Apply methods. Default is
// MinValue for backward compatibility — emit sites are migrated to supply real values
// in follow-up PRs (#651 PR A2).
public record WorkflowStarted(Guid InstanceId, string? ProcessDefinitionId, Guid RootVariablesId,
    DateTimeOffset OccurredAt = default) : IDomainEvent;
public record ExecutionStarted(DateTimeOffset OccurredAt = default) : IDomainEvent;
public record WorkflowCompleted(DateTimeOffset OccurredAt = default) : IDomainEvent;
public record WorkflowCancelled(string Reason, DateTimeOffset OccurredAt = default) : IDomainEvent;

// Activity lifecycle
public record ActivitySpawned(
    Guid ActivityInstanceId, string ActivityId, string ActivityType,
    Guid VariablesId, Guid? ScopeId, int? MultiInstanceIndex,
    Guid? TokenId,
    DateTimeOffset OccurredAt = default) : IDomainEvent;
public record ActivityExecutionStarted(Guid ActivityInstanceId,
    DateTimeOffset OccurredAt = default) : IDomainEvent;
public record ActivityCompleted(
    Guid ActivityInstanceId, Guid VariablesId, ExpandoObject Variables,
    DateTimeOffset OccurredAt = default) : IDomainEvent;
public record ActivityFailed(
    Guid ActivityInstanceId, string ErrorCode, string ErrorMessage,
    DateTimeOffset OccurredAt = default) : IDomainEvent;
public record ActivityExecutionReset(Guid ActivityInstanceId,
    DateTimeOffset OccurredAt = default) : IDomainEvent;
public record ActivityCancelled(Guid ActivityInstanceId, string Reason,
    DateTimeOffset OccurredAt = default) : IDomainEvent;
public record MultiInstanceTotalSet(Guid ActivityInstanceId, int Total,
    DateTimeOffset OccurredAt = default) : IDomainEvent;

// Variable management
public record VariablesMerged(Guid VariablesId, ExpandoObject Variables) : IDomainEvent;
public record ChildVariableScopeCreated(Guid ScopeId, Guid ParentScopeId) : IDomainEvent;
public record VariableScopeCloned(Guid NewScopeId, Guid SourceScopeId) : IDomainEvent;
public record VariableScopesRemoved(IReadOnlyList<Guid> ScopeIds) : IDomainEvent;

/// <summary>
/// Tracks the binding between a scope-host activity instance (SubProcess or
/// Transaction) and its own execution-scope variables id, so that scope
/// completion can disambiguate the host's own scope from sibling
/// EventSubProcess handler scopes that share the host's parent variables id.
/// Emitted from <c>ProcessOpenSubProcess</c>; cleanup is folded into
/// <c>ApplyActivityCompleted</c>/<c>ApplyActivityCancelled</c>.
/// </summary>
public record ScopeHostExecutionScopeOpened(Guid HostInstanceId, Guid ExecutionScopeId) : IDomainEvent;

// Gateway/token management
public record ConditionSequencesAdded(
    Guid GatewayInstanceId, string[] SequenceFlowIds) : IDomainEvent;
public record ConditionSequenceEvaluated(
    Guid GatewayInstanceId, string SequenceFlowId, bool Result) : IDomainEvent;
public record GatewayForkCreated(Guid ForkInstanceId, Guid? ConsumedTokenId) : IDomainEvent;
public record GatewayForkTokenAdded(Guid ForkInstanceId, Guid TokenId) : IDomainEvent;
public record GatewayForkRemoved(Guid ForkInstanceId) : IDomainEvent;
public record ComplexGatewayJoinStateCreated(string GatewayActivityId, Guid FirstActivityInstanceId, string ActivationCondition, Guid WorkflowInstanceId) : IDomainEvent;
public record ComplexGatewayJoinStateTokenIncremented(string GatewayActivityId) : IDomainEvent;
public record ComplexGatewayJoinStateFired(string GatewayActivityId) : IDomainEvent;
public record ComplexGatewayJoinStateRemoved(string GatewayActivityId) : IDomainEvent;

// Parent/child
public record ParentInfoSet(Guid ParentInstanceId, string ParentActivityId) : IDomainEvent;
public record ChildWorkflowLinked(Guid ActivityInstanceId, Guid ChildWorkflowInstanceId) : IDomainEvent;

// User task lifecycle
public record UserTaskRegistered(
    Guid ActivityInstanceId, string? Assignee,
    IReadOnlyList<string> CandidateGroups, IReadOnlyList<string> CandidateUsers,
    IReadOnlyList<string>? ExpectedOutputVariables) : IDomainEvent;
public record UserTaskClaimed(Guid ActivityInstanceId, string UserId, DateTimeOffset ClaimedAt) : IDomainEvent;
public record UserTaskUnclaimed(Guid ActivityInstanceId) : IDomainEvent;
public record UserTaskUnregistered(Guid ActivityInstanceId) : IDomainEvent;
public record UserTaskFailed(Guid ActivityInstanceId, string ActivityId, string ErrorCode, string ErrorMessage) : IDomainEvent;
public record UserTaskCancelled(Guid ActivityInstanceId, string ActivityId, string? Reason) : IDomainEvent;

// Escalation
public record EscalationUncaughtRaised(
    string EscalationCode, string SourceActivityId) : IDomainEvent;

// Timer cycle tracking
public record TimerCycleUpdated(
    Guid HostActivityInstanceId, string TimerActivityId,
    Activities.TimerDefinition? RemainingCycle) : IDomainEvent;

// Conditional event watchers
public record ConditionalWatcherRegistered(
    Guid ActivityInstanceId, string ActivityId, string ConditionExpression,
    Guid VariablesId) : IDomainEvent;
public record ConditionalWatcherFired(Guid ActivityInstanceId) : IDomainEvent;
public record ConditionalWatcherCleared(Guid ActivityInstanceId) : IDomainEvent;
public record ConditionalWatcherResultUpdated(Guid ActivityInstanceId, bool Result) : IDomainEvent;

// Compensation
public record CompensableActivitySnapshotRecorded(
    Guid ActivityInstanceId,
    string ActivityDefinitionId,
    ExpandoObject VariablesSnapshot,
    Guid? ScopeId,
    DateTimeOffset OccurredAt = default) : IDomainEvent;

public record CompensationWalkStarted(
    Guid? ScopeId,
    string? TargetActivityRef,
    int HandlerCount,
    Guid ThrowerActivityInstanceId,
    DateTimeOffset OccurredAt = default) : IDomainEvent;

public record CompensationHandlerSpawned(
    Guid HandlerInstanceId,
    string CompensableActivityDefinitionId,
    string HandlerActivityId,
    Guid? ScopeId,
    DateTimeOffset OccurredAt = default) : IDomainEvent;

public record CompensationEntryMarkedCompensated(
    string ActivityDefinitionId,
    Guid? ScopeId,
    DateTimeOffset OccurredAt = default) : IDomainEvent;

public record CompensationWalkCompleted(Guid? ScopeId,
    DateTimeOffset OccurredAt = default) : IDomainEvent;

public record CompensationWalkFailed(
    Guid? ScopeId,
    Guid HandlerInstanceId,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset OccurredAt = default) : IDomainEvent;

public record CompensationWalkAborted(
    Guid? ScopeId,
    string Reason,
    DateTimeOffset OccurredAt = default) : IDomainEvent;

// Transaction Sub-Process outcome
// Plain record — no [GenerateSerializer] — stored via Newtonsoft.Json in EfCoreEventStore,
// consistent with all other JournaledGrain events in this file.
public record TransactionOutcomeSet(
    Guid TransactionInstanceId,
    States.TransactionOutcome Outcome,
    string? ErrorCode,
    string? ErrorMessage) : IDomainEvent;
