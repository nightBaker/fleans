using System.Dynamic;
using Fleans.Domain.Events;

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
    ExpandoObject InputVariables, string ParentActivityId) : IInfrastructureEffect;

// Parent notifications
public record NotifyParentCompletedEffect(
    Guid ParentInstanceId, string ParentActivityId,
    ExpandoObject Variables) : IInfrastructureEffect;
public record NotifyParentFailedEffect(
    Guid ParentInstanceId, string ParentActivityId,
    Exception Exception) : IInfrastructureEffect;

// Event publishing
public record PublishDomainEventEffect(IDomainEvent Event) : IInfrastructureEffect;
