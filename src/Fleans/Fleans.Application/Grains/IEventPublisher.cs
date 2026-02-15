using Fleans.Domain.Events;

namespace Fleans.Application.Grains;

public interface IEventPublisher : IGrainWithIntegerKey
{
    Task Publish(IDomainEvent domainEvent);
}
