using Fleans.Application.Events.Handlers;
using Fleans.Domain.Events;

namespace Fleans.Infrastructure.EventHandlers;

public class WorfklowEvaluateConditionEventHandler : IWorfklowEvaluateConditionEventHandler
{
    public Task Handle(EvaluateConditionEvent evaluateConditionEvent)
    {
        
        return Task.CompletedTask;
    }
}
