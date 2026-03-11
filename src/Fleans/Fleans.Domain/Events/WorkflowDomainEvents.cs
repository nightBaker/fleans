using System.Dynamic;

namespace Fleans.Domain.Events;

// Workflow lifecycle
public record WorkflowStarted(Guid InstanceId, string? ProcessDefinitionId) : IDomainEvent;
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
public record ActivityCancelled(Guid ActivityInstanceId, string Reason) : IDomainEvent;
public record MultiInstanceTotalSet(Guid ActivityInstanceId, int Total) : IDomainEvent;

// Variable management
public record VariablesMerged(Guid VariablesId, ExpandoObject Variables) : IDomainEvent;
public record ChildVariableScopeCreated(Guid ScopeId, Guid ParentScopeId) : IDomainEvent;
public record VariableScopeCloned(Guid NewScopeId, Guid SourceScopeId) : IDomainEvent;
public record VariableScopesRemoved(List<Guid> ScopeIds) : IDomainEvent;

// Gateway/token management
public record ConditionSequencesAdded(
    Guid GatewayInstanceId, string[] SequenceFlowIds) : IDomainEvent;
public record ConditionSequenceEvaluated(
    Guid GatewayInstanceId, string SequenceFlowId, bool Result) : IDomainEvent;
public record GatewayForkCreated(Guid ForkInstanceId, Guid? ConsumedTokenId) : IDomainEvent;
public record GatewayForkTokenAdded(Guid ForkInstanceId, Guid TokenId) : IDomainEvent;
public record GatewayForkRemoved(Guid ForkInstanceId) : IDomainEvent;

// Parent/child
public record ParentInfoSet(Guid ParentInstanceId, string ParentActivityId) : IDomainEvent;
