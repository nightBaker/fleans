using System.Dynamic;

namespace Fleans.Domain.Events;

/// <summary>
/// Base of every event in the <c>WorkflowInstance</c> journal. Events are immutable facts: an
/// <c>Apply</c> handler reads only the event and the current state — never the clock, never
/// <c>Guid.NewGuid()</c> — so replaying the journal rebuilds exactly the state the live run had.
/// </summary>
/// <remarks>
/// <see cref="OccurredAt"/> is stamped by <c>WorkflowExecution.Stamp</c> (via <c>Emit</c>, or the
/// grain's durable enqueue); event sites never set it.
/// Handlers that record a time (<c>CreatedAt</c>, <c>CompletedAt</c>, …) take it from here.
/// <para>
/// The journal stores events in Orleans binary form (#867), so every event is
/// <c>[GenerateSerializer]</c> with explicit <c>[Id]</c>s and a short <c>[Alias]</c> — the alias is
/// the type tag written into the journal, so keep it short and never reuse one. Collections are
/// <c>List&lt;T&gt;</c> (no Orleans codec for collection-expression arrays).
/// </para>
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
[GenerateSerializer, Alias("ev.WorkflowStarted")]
public record WorkflowStarted([property: Id(0)] Guid InstanceId, [property: Id(1)] string? ProcessDefinitionId, [property: Id(2)] Guid RootVariablesId,
    [property: Id(3)] string? BusinessId = null) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ExecutionStarted")]
public record ExecutionStarted : WorkflowEvent;
[GenerateSerializer, Alias("ev.WorkflowCompleted")]
public record WorkflowCompleted : WorkflowEvent;
[GenerateSerializer, Alias("ev.WorkflowCancelled")]
public record WorkflowCancelled([property: Id(0)] string Reason) : WorkflowEvent;
/// <summary>
/// The instance terminated because an unhandled activity failure left it with no live
/// tokens (no active activities, no completed root end event). Carries the failure that
/// ended it so the terminal state is self-describing (#762).
/// </summary>
[GenerateSerializer, Alias("ev.WorkflowFailed")]
public record WorkflowFailed(
    [property: Id(0)] string FailedActivityId, [property: Id(1)] Guid FailedActivityInstanceId, [property: Id(2)] string ErrorCode, [property: Id(3)] string ErrorMessage) : WorkflowEvent;

// Activity lifecycle
[GenerateSerializer, Alias("ev.ActivitySpawned")]
public record ActivitySpawned(
    [property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] string ActivityId, [property: Id(2)] string ActivityType,
    [property: Id(3)] Guid VariablesId, [property: Id(4)] Guid? ScopeId, [property: Id(5)] int? MultiInstanceIndex,
    [property: Id(6)] Guid? TokenId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ActivityExecutionStarted")]
public record ActivityExecutionStarted([property: Id(0)] Guid ActivityInstanceId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ActivityCompleted")]
public record ActivityCompleted(
    [property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] Guid VariablesId, [property: Id(2)] ExpandoObject Variables) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ActivityFailed")]
public record ActivityFailed(
    [property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] string ErrorCode, [property: Id(2)] string ErrorMessage) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ActivityExecutionReset")]
public record ActivityExecutionReset([property: Id(0)] Guid ActivityInstanceId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ActivityCancelled")]
public record ActivityCancelled([property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] string Reason) : WorkflowEvent;
[GenerateSerializer, Alias("ev.MultiInstanceTotalSet")]
public record MultiInstanceTotalSet([property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] int Total) : WorkflowEvent;

// Variable management
[GenerateSerializer, Alias("ev.VariablesMerged")]
public record VariablesMerged([property: Id(0)] Guid VariablesId, [property: Id(1)] ExpandoObject Variables) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ChildVariableScopeCreated")]
public record ChildVariableScopeCreated([property: Id(0)] Guid ScopeId, [property: Id(1)] Guid ParentScopeId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.VariableScopeCloned")]
public record VariableScopeCloned([property: Id(0)] Guid NewScopeId, [property: Id(1)] Guid SourceScopeId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.VariableScopesRemoved")]
public record VariableScopesRemoved([property: Id(0)] List<Guid> ScopeIds) : WorkflowEvent;

/// <summary>
/// Tracks the binding between a scope-host activity instance (SubProcess or
/// Transaction) and its own execution-scope variables id, so that scope
/// completion can disambiguate the host's own scope from sibling
/// EventSubProcess handler scopes that share the host's parent variables id.
/// Emitted from <c>ProcessOpenSubProcess</c>; cleanup is folded into
/// <c>ApplyActivityCompleted</c>/<c>ApplyActivityCancelled</c>.
/// </summary>
[GenerateSerializer, Alias("ev.ScopeHostExecutionScopeOpened")]
public record ScopeHostExecutionScopeOpened([property: Id(0)] Guid HostInstanceId, [property: Id(1)] Guid ExecutionScopeId) : WorkflowEvent;

// Gateway/token management
[GenerateSerializer, Alias("ev.ConditionSequencesAdded")]
public record ConditionSequencesAdded(
    [property: Id(0)] Guid GatewayInstanceId, [property: Id(1)] string[] SequenceFlowIds) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ConditionSequenceEvaluated")]
public record ConditionSequenceEvaluated(
    [property: Id(0)] Guid GatewayInstanceId, [property: Id(1)] string SequenceFlowId, [property: Id(2)] bool Result) : WorkflowEvent;
[GenerateSerializer, Alias("ev.GatewayForkCreated")]
public record GatewayForkCreated([property: Id(0)] Guid ForkInstanceId, [property: Id(1)] Guid? ConsumedTokenId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.GatewayForkTokenAdded")]
public record GatewayForkTokenAdded([property: Id(0)] Guid ForkInstanceId, [property: Id(1)] Guid TokenId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.GatewayForkRemoved")]
public record GatewayForkRemoved([property: Id(0)] Guid ForkInstanceId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ComplexGatewayJoinStateCreated")]
public record ComplexGatewayJoinStateCreated([property: Id(0)] string GatewayActivityId, [property: Id(1)] Guid FirstActivityInstanceId, [property: Id(2)] string ActivationCondition, [property: Id(3)] Guid WorkflowInstanceId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ComplexGatewayJoinStateTokenIncremented")]
public record ComplexGatewayJoinStateTokenIncremented([property: Id(0)] string GatewayActivityId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ComplexGatewayJoinStateFired")]
public record ComplexGatewayJoinStateFired([property: Id(0)] string GatewayActivityId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ComplexGatewayJoinStateRemoved")]
public record ComplexGatewayJoinStateRemoved([property: Id(0)] string GatewayActivityId) : WorkflowEvent;

// Parent/child
[GenerateSerializer, Alias("ev.ParentInfoSet")]
public record ParentInfoSet([property: Id(0)] Guid ParentInstanceId, [property: Id(1)] string ParentActivityId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ChildWorkflowLinked")]
public record ChildWorkflowLinked([property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] Guid ChildWorkflowInstanceId) : WorkflowEvent;

// User task lifecycle
[GenerateSerializer, Alias("ev.UserTaskRegistered")]
public record UserTaskRegistered(
    [property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] string? Assignee,
    [property: Id(2)] List<string> CandidateGroups, [property: Id(3)] List<string> CandidateUsers,
    [property: Id(4)] List<string>? ExpectedOutputVariables) : WorkflowEvent;
[GenerateSerializer, Alias("ev.UserTaskClaimed")]
public record UserTaskClaimed([property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] string UserId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.UserTaskUnclaimed")]
public record UserTaskUnclaimed([property: Id(0)] Guid ActivityInstanceId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.UserTaskUnregistered")]
public record UserTaskUnregistered([property: Id(0)] Guid ActivityInstanceId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.UserTaskFailed")]
public record UserTaskFailed([property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] string ActivityId, [property: Id(2)] string ErrorCode, [property: Id(3)] string ErrorMessage) : WorkflowEvent;
[GenerateSerializer, Alias("ev.UserTaskCancelled")]
public record UserTaskCancelled([property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] string ActivityId, [property: Id(2)] string? Reason) : WorkflowEvent;

// Escalation
[GenerateSerializer, Alias("ev.EscalationUncaughtRaised")]
public record EscalationUncaughtRaised(
    [property: Id(0)] string EscalationCode, [property: Id(1)] string SourceActivityId) : WorkflowEvent;

// Timer cycle tracking
[GenerateSerializer, Alias("ev.TimerCycleUpdated")]
public record TimerCycleUpdated(
    [property: Id(0)] Guid HostActivityInstanceId, [property: Id(1)] string TimerActivityId,
    [property: Id(2)] Activities.TimerDefinition? RemainingCycle) : WorkflowEvent;

// Conditional event watchers
[GenerateSerializer, Alias("ev.ConditionalWatcherRegistered")]
public record ConditionalWatcherRegistered(
    [property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] string ActivityId, [property: Id(2)] string ConditionExpression,
    [property: Id(3)] Guid VariablesId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ConditionalWatcherFired")]
public record ConditionalWatcherFired([property: Id(0)] Guid ActivityInstanceId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ConditionalWatcherCleared")]
public record ConditionalWatcherCleared([property: Id(0)] Guid ActivityInstanceId) : WorkflowEvent;
[GenerateSerializer, Alias("ev.ConditionalWatcherResultUpdated")]
public record ConditionalWatcherResultUpdated([property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] bool Result) : WorkflowEvent;

// Compensation
[GenerateSerializer, Alias("ev.CompensableActivitySnapshotRecorded")]
public record CompensableActivitySnapshotRecorded(
    [property: Id(0)] Guid ActivityInstanceId,
    [property: Id(1)] string ActivityDefinitionId,
    [property: Id(2)] ExpandoObject VariablesSnapshot,
    [property: Id(3)] Guid? ScopeId) : WorkflowEvent;

[GenerateSerializer, Alias("ev.CompensationWalkStarted")]
public record CompensationWalkStarted(
    [property: Id(0)] Guid? ScopeId,
    [property: Id(1)] string? TargetActivityRef,
    [property: Id(2)] int HandlerCount,
    [property: Id(3)] Guid ThrowerActivityInstanceId) : WorkflowEvent;

[GenerateSerializer, Alias("ev.CompensationHandlerSpawned")]
public record CompensationHandlerSpawned(
    [property: Id(0)] Guid HandlerInstanceId,
    [property: Id(1)] string CompensableActivityDefinitionId,
    [property: Id(2)] string HandlerActivityId,
    [property: Id(3)] Guid? ScopeId) : WorkflowEvent;

[GenerateSerializer, Alias("ev.CompensationEntryMarkedCompensated")]
public record CompensationEntryMarkedCompensated(
    [property: Id(0)] string ActivityDefinitionId,
    [property: Id(1)] Guid? ScopeId) : WorkflowEvent;

[GenerateSerializer, Alias("ev.CompensationWalkCompleted")]
public record CompensationWalkCompleted([property: Id(0)] Guid? ScopeId) : WorkflowEvent;

[GenerateSerializer, Alias("ev.CompensationWalkFailed")]
public record CompensationWalkFailed(
    [property: Id(0)] Guid? ScopeId,
    [property: Id(1)] Guid HandlerInstanceId,
    [property: Id(2)] string ErrorCode,
    [property: Id(3)] string ErrorMessage) : WorkflowEvent;

[GenerateSerializer, Alias("ev.CompensationWalkAborted")]
public record CompensationWalkAborted(
    [property: Id(0)] Guid? ScopeId,
    [property: Id(1)] string Reason) : WorkflowEvent;

// Transaction Sub-Process outcome
[GenerateSerializer, Alias("ev.TransactionOutcomeSet")]
public record TransactionOutcomeSet(
    [property: Id(0)] Guid TransactionInstanceId,
    [property: Id(1)] States.TransactionOutcome Outcome,
    [property: Id(2)] string? ErrorCode,
    [property: Id(3)] string? ErrorMessage) : WorkflowEvent;
