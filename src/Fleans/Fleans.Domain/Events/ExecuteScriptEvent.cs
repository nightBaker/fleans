namespace Fleans.Domain.Events;

[GenerateSerializer]
public record ExecuteScriptEvent(Guid WorkflowInstanceId,
                                 string WorkflowId,
                                 Guid ActivityInstanceId,
                                 string ActivityId,
                                 string Script,
                                 string ScriptFormat) : IDomainEvent;
