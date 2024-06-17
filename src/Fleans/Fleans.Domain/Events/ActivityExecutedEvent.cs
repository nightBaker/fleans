namespace Fleans.Domain.Events
{
    [GenerateSerializer]
    public record WorkflowActivityExecutedEvent(Guid WorkflowInstanceId,
                                                string WorkflowId,
                                                Guid ActivityInstanceId,
                                                string activityId,
                                                string TypeName) : IDomainEvent;    
}
