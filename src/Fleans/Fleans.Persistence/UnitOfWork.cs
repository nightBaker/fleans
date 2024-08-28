using Fleans.Persistence;
using MediatR;
using ProjectTemplate.APPLICATION.Interfaces.Persistence;
using ProjectTemplate.DOMAIN.Events;
using ProjectTemplate.DOMAIN.SeedWork;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectTemplate.PERSISTENCE
{
    public class UnitOfWork : IUnitOfWork
    {
        readonly FleansDbContext _context;
        readonly IMediator _mediator;
        
        public UnitOfWork(FleansDbContext context, IMediator mediator)
        {
            _context = context;
            _mediator = mediator;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var (preSaveEvents, postSaveEvents) = _getAndClearDomainEvents();

            await _dispatchDomainEventsAsync(preSaveEvents);

            var result = await _context.SaveChangesAsync();

            await _dispatchDomainEventsAsync(postSaveEvents);

            return result;
        }

        private (IList<INotification>, IList<INotification>) _getAndClearDomainEvents()
        {
            var domainEntities = _context.ChangeTracker
               .Entries<Entity>()
               .Where(x => x.Entity.DomainEvents != null && x.Entity.DomainEvents.Any());

            var domainPostSaveEvents = domainEntities
                .SelectMany(x => x.Entity.DomainEvents.Where(domainEvent => domainEvent is IPostSaveEvent))
                .ToList();

            var domainPreSaveEvents = domainEntities
                .SelectMany(x => x.Entity.DomainEvents.Where(domainEvent => domainEvent! is IPreSaveEvent))
                .ToList();

            domainEntities.ToList()
                .ForEach(entitty => entitty.Entity.ClearDomainEvents());

            return (domainPreSaveEvents, domainPostSaveEvents);
        }

        private async Task _dispatchDomainEventsAsync(IEnumerable<INotification> domainEvents)
        {
            var tasks = domainEvents
                .Select(async (domainEvent) =>
                {
                    await _mediator.Publish(domainEvent);
                });

            await Task.WhenAll(tasks);
        }
    }
}
