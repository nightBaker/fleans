using System.Dynamic;

namespace Fleans.Domain.Events;

// Workflow lifecycle
public record WorkflowStarted(Guid InstanceId, string? ProcessDefinitionId, Guid RootVariablesId) : IDomainEvent;
public record ExecutionStarted() : IDomainEvent;
public record WorkflowCompleted() : IDomainEvent;

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
public record ComplexGatewayJoinStateCreated(Guid ActivityInstanceId, string ActivationCondition, Guid WorkflowInstanceId) : IDomainEvent;
public record ComplexGatewayJoinStateTokenIncremented(Guid ActivityInstanceId) : IDomainEvent;
public record ComplexGatewayJoinStateFired(Guid ActivityInstanceId) : IDomainEvent;
public record ComplexGatewayJoinStateRemoved(Guid ActivityInstanceId) : IDomainEvent;

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

// Timer cycle tracking
public record TimerCycleUpdated(
    Guid HostActivityInstanceId, string TimerActivityId,
    Activities.TimerDefinition? RemainingCycle) : IDomainEvent;
