using Fleans.Domain.Events;

namespace Fleans.Application.Events.Handlers;

public interface IWorfklowEvaluateConditionEventHandler : IGrainObserver
{
    Task Handle(EvaluateConditionEvent domainEvent);
}


