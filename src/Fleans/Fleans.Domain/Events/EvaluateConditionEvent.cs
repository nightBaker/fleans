namespace Fleans.Domain.Events;

[GenerateSerializer]
public record EvaluateConditionEvent(Guid WorkflowInstanceId,
                                     string WorkflowId,
                                     Guid ActivityInstanceId,
                                     string ActivityId,
                                     string SequenceFlowId,
                                     string Condition) : IDomainEvent;
