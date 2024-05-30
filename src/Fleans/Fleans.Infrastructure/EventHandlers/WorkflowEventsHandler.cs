using Fleans.Application.Events.Handlers;
using Fleans.Domain.Events;

namespace Fleans.Infrastructure.EventHandlers
{
    public class WorkflowEventsHandler : IWorkflowEventsHandler
    {
        public Task Handle(IDomainEvent domainEvent)
        {
            Console.WriteLine($"Event handled: {domainEvent}");
            return Task.CompletedTask;
        }
    }
}
