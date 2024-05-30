namespace Fleans.Domain.Events;

public record EvaluateConditionEvent(Guid WorkflowInstanceId,
                                     string WorkflowId,
                                     Guid ActivityInstanceId,
                                     string ActivityId,
                                     string SequenceFlowId,
                                     string Condition) : IDomainEvent;
