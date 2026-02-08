namespace Fleans.Domain.Events;

[GenerateSerializer]
public record ExecuteScriptEvent(Guid WorkflowInstanceId,
                                 string WorkflowId,
                                 string? ProcessDefinitionId,
                                 Guid ActivityInstanceId,
                                 string ActivityId,
                                 string Script,
                                 string ScriptFormat) : IDomainEvent;
