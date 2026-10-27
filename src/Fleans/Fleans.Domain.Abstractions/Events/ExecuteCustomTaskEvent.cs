using System.Dynamic;

namespace Fleans.Domain.Events;

[GenerateSerializer]
public record ExecuteCustomTaskEvent(
    Guid WorkflowInstanceId,
    string WorkflowId,
    string? ProcessDefinitionId,
    Guid ActivityInstanceId,
    string ActivityId,
    string TaskType,
    ExpandoObject Inputs,
    Guid VariablesId,
    string? BusinessId) : IDomainEvent;
