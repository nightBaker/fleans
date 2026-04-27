using Fleans.Domain.Activities;

namespace Fleans.Domain.Events;

[GenerateSerializer]
public record ExecuteCustomTaskEvent(
    Guid WorkflowInstanceId,
    string WorkflowId,
    string? ProcessDefinitionId,
    Guid ActivityInstanceId,
    string ActivityId,
    string TaskType,
    List<InputMapping> InputMappings,
    List<OutputMapping> OutputMappings,
    Guid VariablesId) : IDomainEvent;
