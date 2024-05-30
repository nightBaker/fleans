using Fleans.Application.Events.Handlers;
using Fleans.Application.WorkflowGrains;
using Fleans.Domain.Events;
using Orleans.Utilities;

namespace Fleans.Application.Events;

public class WorkflowEventsPublisher : Grain, IWorkflowEventsPublisher
{
    private readonly ObserverManager<IWorkflowEventsHandler> _subsManager;
    private readonly ObserverManager<IWorfklowEvaluateConditionEventHandler> _worfklowEvaluateConditionEventHandlers;

    public WorkflowEventsPublisher(ObserverManager<IWorkflowEventsHandler> subsManager, ObserverManager<IWorfklowEvaluateConditionEventHandler> worfklowEvaluateConditionEventHandlers)
    {
        _subsManager = subsManager;
        _worfklowEvaluateConditionEventHandlers = worfklowEvaluateConditionEventHandlers;
    }

    public void Publish(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case EvaluateConditionEvent evaluateConditionEvent:
                _worfklowEvaluateConditionEventHandlers.Notify(x => x.Handle(evaluateConditionEvent));
                break;
            default:
                _subsManager.Notify(x => x.Handle(domainEvent));
                break;
        }
    }

    public Task Subscribe(IWorkflowEventsHandler observer)
    {
        _subsManager.Subscribe(observer, observer);

        return Task.CompletedTask;
    }

    public Task Subscribe(IWorfklowEvaluateConditionEventHandler observer)
    {
        _worfklowEvaluateConditionEventHandlers.Subscribe(observer, observer);

        return Task.CompletedTask;
    }
}
