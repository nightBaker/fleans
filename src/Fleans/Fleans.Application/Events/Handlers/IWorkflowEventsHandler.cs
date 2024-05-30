using Fleans.Domain.Events;

namespace Fleans.Application.Events.Handlers;

public interface IWorkflowEventsHandler : IGrainObserver
{
    Task Handle(IDomainEvent domainEvent);
}


