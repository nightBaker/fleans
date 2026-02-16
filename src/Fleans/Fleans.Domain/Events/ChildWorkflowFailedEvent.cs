namespace Fleans.Domain.Events;

[GenerateSerializer]
public record ChildWorkflowFailedEvent(
    [property: Id(0)] Guid ParentWorkflowInstanceId,
    [property: Id(1)] string ParentActivityId,
    [property: Id(2)] string WorkflowId,
    [property: Id(3)] string? ProcessDefinitionId,
    [property: Id(4)] int ErrorCode,
    [property: Id(5)] string ErrorMessage) : IDomainEvent;
