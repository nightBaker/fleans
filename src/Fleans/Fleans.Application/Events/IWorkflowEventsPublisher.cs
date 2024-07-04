using Fleans.Application.Events.Handlers;
using Fleans.Domain.Events;

namespace Fleans.Application.Events;

public interface IWorkflowEventsPublisher : IGrainWithIntegerKey
{
    Task Publish(IDomainEvent domainEvent);
}


