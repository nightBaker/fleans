using Fleans.Domain.Events;

namespace Fleans.Domain;

public interface IActivityExecutionContext
{
    ValueTask<Guid> GetActivityInstanceId();
    ValueTask<string> GetActivityId();
    ValueTask<bool> IsCompleted();
    ValueTask Complete();
    ValueTask Execute();
    ValueTask PublishEvent(IDomainEvent domainEvent);
}
