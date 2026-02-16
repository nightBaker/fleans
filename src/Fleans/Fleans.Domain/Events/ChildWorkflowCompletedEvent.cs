using System.Dynamic;

namespace Fleans.Domain.Events;

[GenerateSerializer]
public record ChildWorkflowCompletedEvent(
    [property: Id(0)] Guid ParentWorkflowInstanceId,
    [property: Id(1)] string ParentActivityId,
    [property: Id(2)] string WorkflowId,
    [property: Id(3)] string? ProcessDefinitionId,
    [property: Id(4)] ExpandoObject ChildVariables) : IDomainEvent;
