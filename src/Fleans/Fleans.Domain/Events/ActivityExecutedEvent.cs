namespace Fleans.Domain.Events
{
    public record ActivityExecutedEvent(string ActivityId, string TypeName) : IDomainEvent;    
}
