namespace Fleans.Domain.Events
{
    public record WorkflowActivityExecutedEvent(Guid WorkflowInstanceId,
                                                string WorkflowId,
                                                Guid ActivityInstanceId,
                                                string activityId,
                                                string TypeName) : IDomainEvent;    
}
