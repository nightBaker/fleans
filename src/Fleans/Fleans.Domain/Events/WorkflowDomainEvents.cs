using System.Dynamic;

namespace Fleans.Domain.Events;

// Workflow lifecycle
public record WorkflowStarted(Guid InstanceId, string? ProcessDefinitionId, Guid RootVariablesId) : IDomainEvent;
public record ExecutionStarted() : IDomainEvent;
public record WorkflowCompleted() : IDomainEvent;
public record WorkflowCancelled(string Reason) : IDomainEvent;

// Activity lifecycle
public record ActivitySpawned(
    Guid ActivityInstanceId, string ActivityId, string ActivityType,
    Guid VariablesId, Guid? ScopeId, int? MultiInstanceIndex,
    Guid? TokenId) : IDomainEvent;
public record ActivityExecutionStarted(Guid ActivityInstanceId) : IDomainEvent;
public record ActivityCompleted(
    Guid ActivityInstanceId, Guid VariablesId, ExpandoObject Variables) : IDomainEvent;
public record ActivityFailed(
    Guid ActivityInstanceId, int ErrorCode, string ErrorMessage) : IDomainEvent;
public record ActivityExecutionReset(Guid ActivityInstanceId) : IDomainEvent;
public record ActivityCancelled(Guid ActivityInstanceId, string Reason) : IDomainEvent;
public record MultiInstanceTotalSet(Guid ActivityInstanceId, int Total) : IDomainEvent;

// Variable management
public record VariablesMerged(Guid VariablesId, ExpandoObject Variables) : IDomainEvent;
public record ChildVariableScopeCreated(Guid ScopeId, Guid ParentScopeId) : IDomainEvent;
public record VariableScopeCloned(Guid NewScopeId, Guid SourceScopeId) : IDomainEvent;
public record VariableScopesRemoved(IReadOnlyList<Guid> ScopeIds) : IDomainEvent;

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
    Guid? ScopeId) : IDomainEvent;

public record CompensationWalkStarted(
    Guid? ScopeId,
    string? TargetActivityRef,
    int HandlerCount,
    Guid ThrowerActivityInstanceId) : IDomainEvent;

public record CompensationHandlerSpawned(
    Guid HandlerInstanceId,
    string CompensableActivityDefinitionId,
    string HandlerActivityId,
    Guid? ScopeId) : IDomainEvent;

public record CompensationEntryMarkedCompensated(
    string ActivityDefinitionId,
    Guid? ScopeId) : IDomainEvent;

public record CompensationWalkCompleted(Guid? ScopeId) : IDomainEvent;

public record CompensationWalkFailed(
    Guid? ScopeId,
    Guid HandlerInstanceId,
    int ErrorCode,
    string ErrorMessage) : IDomainEvent;

// Transaction Sub-Process outcome
// Plain record — no [GenerateSerializer] — stored via Newtonsoft.Json in EfCoreEventStore,
// consistent with all other JournaledGrain events in this file.
public record TransactionOutcomeSet(
    Guid TransactionInstanceId,
    States.TransactionOutcome Outcome,
    int? ErrorCode,
    string? ErrorMessage) : IDomainEvent;
