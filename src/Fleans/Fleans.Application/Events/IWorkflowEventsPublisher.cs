using Fleans.Application.Events.Handlers;
using Fleans.Domain.Events;

namespace Fleans.Application.Events;

public interface IWorkflowEventsPublisher : IGrainWithIntegerKey, IEventPublisher
{
    Task Subscribe(IWorkflowEventsHandler observer);
    Task Subscribe(IWorfklowEvaluateConditionEventHandler observer);
}


