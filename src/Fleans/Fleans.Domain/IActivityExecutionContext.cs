using Fleans.Domain.Events;

namespace Fleans.Domain;

public interface IActivityExecutionContext
{
    ValueTask<Guid> GetActivityInstanceId();
    ValueTask<string> GetActivityId();
    ValueTask<Guid> GetVariablesStateId();
    ValueTask<int?> GetMultiInstanceIndex();
    ValueTask<int?> GetMultiInstanceTotal();
    ValueTask SetMultiInstanceTotal(int total);
    ValueTask<bool> IsCompleted();
    ValueTask<Guid?> GetTokenId();
    ValueTask Complete();
    ValueTask Execute();
    ValueTask PublishEvent(IDomainEvent domainEvent);
}
