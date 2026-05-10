namespace Fleans.Domain.Events;

[GenerateSerializer]
public record EvaluateCompletionConditionEvent(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] string WorkflowId,
    [property: Id(2)] string? ProcessDefinitionId,
    [property: Id(3)] Guid HostActivityInstanceId,
    [property: Id(4)] string HostActivityId,
    [property: Id(5)] string Condition,
    [property: Id(6)] int NrOfInstances,
    [property: Id(7)] int NrOfActiveInstances,
    [property: Id(8)] int NrOfCompletedInstances) : IDomainEvent;
