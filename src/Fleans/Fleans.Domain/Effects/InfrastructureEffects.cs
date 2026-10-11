using System.Dynamic;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Domain.Effects;

public interface IInfrastructureEffect { }

// Timer
public record RegisterTimerEffect(
    Guid WorkflowInstanceId, Guid HostActivityInstanceId,
    string TimerActivityId, TimeSpan DueTime) : IInfrastructureEffect;
public record UnregisterTimerEffect(
    Guid WorkflowInstanceId, Guid HostActivityInstanceId,
    string TimerActivityId) : IInfrastructureEffect;

// Message
public record SubscribeMessageEffect(
    string MessageName, string CorrelationKey,
    Guid WorkflowInstanceId, string ActivityId, Guid HostActivityInstanceId) : IInfrastructureEffect;
public record UnsubscribeMessageEffect(
    string MessageName, string CorrelationKey) : IInfrastructureEffect;

// Signal
public record SubscribeSignalEffect(
    string SignalName, Guid WorkflowInstanceId,
    string ActivityId, Guid HostActivityInstanceId) : IInfrastructureEffect;
public record UnsubscribeSignalEffect(
    string SignalName, Guid WorkflowInstanceId, string ActivityId) : IInfrastructureEffect;
public record ThrowSignalEffect(string SignalName) : IInfrastructureEffect;

// Child workflows
public record StartChildWorkflowEffect(
    Guid ChildInstanceId, string ProcessDefinitionKey,
    ExpandoObject InputVariables, string ParentActivityId,
    Guid ParentActivityInstanceId) : IInfrastructureEffect;

// Parent notifications
public record NotifyParentCompletedEffect(
    Guid ParentInstanceId, string ParentActivityId,
    ExpandoObject Variables) : IInfrastructureEffect;
public record NotifyParentFailedEffect(
    Guid ParentInstanceId, string ParentActivityId,
    Exception Exception) : IInfrastructureEffect;

// User task
public record RegisterUserTaskEffect(
    Guid WorkflowInstanceId, Guid ActivityInstanceId, string ActivityId,
    string? Assignee, IReadOnlyList<string> CandidateGroups,
    IReadOnlyList<string> CandidateUsers,
    IReadOnlyList<string>? ExpectedOutputVariables) : IInfrastructureEffect;
public record CompleteUserTaskPersistenceEffect(Guid ActivityInstanceId) : IInfrastructureEffect;
public record UpdateUserTaskClaimEffect(
    Guid ActivityInstanceId, string? ClaimedBy,
    UserTaskLifecycleState TaskState) : IInfrastructureEffect;

// Escalation
// EscalationInstanceId carries the originating throw's activity-instance id (a raw domain Guid)
// so the application layer can derive a deterministic, per-throw op-id for dedup across retries
// and re-escalation hops (#657). It stays a Guid here — the "child-escalation:{guid}" string is
// formatted in the application layer per the domain-purity rule.
public record NotifyParentEscalationRaisedEffect(
    Guid ParentWorkflowInstanceId,
    Guid ChildWorkflowInstanceId,
    string HostActivityId,
    string EscalationCode,
    ExpandoObject Variables,
    Guid EscalationInstanceId) : IInfrastructureEffect;

// Event publishing
public record PublishDomainEventEffect(IDomainEvent Event) : IInfrastructureEffect;
