namespace Fleans.Domain.Events;

[GenerateSerializer]
public record EvaluateActivationConditionEvent(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] string WorkflowId,
    [property: Id(2)] string? ProcessDefinitionId,
    [property: Id(3)] Guid ActivityInstanceId,
    [property: Id(4)] string ActivityId,
    [property: Id(5)] string Condition,
    [property: Id(6)] Guid VariablesId,
    [property: Id(7)] int NrOfToken) : IDomainEvent;
