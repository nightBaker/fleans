using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Effects;

public partial class WorkflowLifecycleEffectHandler : IEffectHandler
{
    private readonly ILogger<WorkflowLifecycleEffectHandler> _logger;

    public WorkflowLifecycleEffectHandler(ILogger<WorkflowLifecycleEffectHandler> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(IInfrastructureEffect effect) =>
        effect is StartChildWorkflowEffect or NotifyParentCompletedEffect
            or NotifyParentFailedEffect or NotifyParentEscalationRaisedEffect
            or PublishDomainEventEffect;

    public async Task HandleAsync(IInfrastructureEffect effect, IEffectContext context)
    {
        switch (effect)
        {
            case StartChildWorkflowEffect startChild:
                await PerformStartChildWorkflow(startChild, context);
                break;

            case NotifyParentCompletedEffect notifyCompleted:
                var parentGrain = context.GrainFactory.GetGrain<IWorkflowInstanceGrain>(notifyCompleted.ParentInstanceId);
                await parentGrain.OnChildWorkflowCompleted(notifyCompleted.ParentActivityId, notifyCompleted.Variables);
                break;

            case NotifyParentFailedEffect notifyFailed:
                var parentFailGrain = context.GrainFactory.GetGrain<IWorkflowInstanceGrain>(notifyFailed.ParentInstanceId);
                await parentFailGrain.OnChildWorkflowFailed(notifyFailed.ParentActivityId, notifyFailed.Exception);
                break;

            case NotifyParentEscalationRaisedEffect escalation:
                var parentEscGrain = context.GrainFactory.GetGrain<IWorkflowInstanceGrain>(escalation.ParentWorkflowInstanceId);
                var escalationResult = await parentEscGrain.OnChildEscalationRaised(
                    escalation.ChildWorkflowInstanceId, escalation.HostActivityId,
                    escalation.EscalationCode, escalation.Variables);
                context.SetEscalationParentResult(escalationResult);
                break;

            case PublishDomainEventEffect publishEvt:
                var eventPublisher = context.GrainFactory.GetGrain<IEventPublisher>(0);
                await eventPublisher.Publish(publishEvt.Event);
                break;

            default:
                throw new InvalidOperationException($"Unexpected effect type in {nameof(WorkflowLifecycleEffectHandler)}: {effect.GetType().Name}");
        }
    }

    private async Task PerformStartChildWorkflow(StartChildWorkflowEffect startChild, IEffectContext context)
    {
        var processGrain = context.GrainFactory.GetGrain<IProcessDefinitionGrain>(startChild.ProcessDefinitionKey);
        var childDefinition = await processGrain.GetLatestDefinition();

        var child = context.GrainFactory.GetGrain<IWorkflowInstanceGrain>(startChild.ChildInstanceId);

        LogStartingChildWorkflow(startChild.ProcessDefinitionKey, startChild.ChildInstanceId);

        await child.SetWorkflow(childDefinition);
        await child.SetParentInfo(context.WorkflowInstanceId, startChild.ParentActivityId);

        if (((IDictionary<string, object?>)startChild.InputVariables).Count > 0)
            await child.SetInitialVariables(startChild.InputVariables);

        await child.StartWorkflow();
    }

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information,
        Message = "Starting child workflow: CalledProcessKey={CalledProcessKey}, ChildId={ChildId}")]
    private partial void LogStartingChildWorkflow(string calledProcessKey, Guid childId);
}
