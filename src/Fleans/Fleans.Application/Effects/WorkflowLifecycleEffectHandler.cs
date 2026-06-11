using Fleans.Application.Grains;
using Fleans.Domain;
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
                // Op-id is deterministic per logical call so a retried RPC dedups (#657).
                var completedOpId = GrainFactoryRetryExtensions.ChildCompletedOpId(
                    context.WorkflowInstanceId, notifyCompleted.ParentActivityId);
                await context.GrainFactory.CallWithRetry<IWorkflowInstanceGrain>(
                    notifyCompleted.ParentInstanceId, completedOpId,
                    g => g.OnChildWorkflowCompleted(completedOpId, notifyCompleted.ParentActivityId, notifyCompleted.Variables));
                break;

            case NotifyParentFailedEffect notifyFailed:
                var failedOpId = GrainFactoryRetryExtensions.ChildFailedOpId(
                    context.WorkflowInstanceId, notifyFailed.ParentActivityId);
                await context.GrainFactory.CallWithRetry<IWorkflowInstanceGrain>(
                    notifyFailed.ParentInstanceId, failedOpId,
                    g => g.OnChildWorkflowFailed(failedOpId, notifyFailed.ParentActivityId, notifyFailed.Exception));
                break;

            case NotifyParentEscalationRaisedEffect escalation:
                // Op-id is derived once at the origin throw (carried as a raw Guid on the effect) and
                // formatted here in the application layer; a re-escalated hop reuses the same id (#657).
                var escalationOpId = GrainFactoryRetryExtensions.ChildEscalationOpId(escalation.EscalationInstanceId);
                var escalationResult = await context.GrainFactory.CallWithRetry<IWorkflowInstanceGrain, EscalationHandledResult>(
                    escalation.ParentWorkflowInstanceId, escalationOpId,
                    g => g.OnChildEscalationRaised(
                        escalationOpId, escalation.EscalationInstanceId,
                        escalation.ChildWorkflowInstanceId, escalation.HostActivityId,
                        escalation.EscalationCode, escalation.Variables));
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
        try
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
        catch (Exception ex)
        {
            LogChildStartFailed(
                context.WorkflowInstanceId,
                startChild.ChildInstanceId,
                startChild.ProcessDefinitionKey,
                ex.Message);
            await context.ProcessFailureEffects(
                startChild.ParentActivityId,
                startChild.ParentActivityInstanceId,
                ex);
        }
    }

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information,
        Message = "Starting child workflow: CalledProcessKey={CalledProcessKey}, ChildId={ChildId}")]
    private partial void LogStartingChildWorkflow(string calledProcessKey, Guid childId);

    [LoggerMessage(EventId = 4070, Level = LogLevel.Error,
        Message = "Child workflow start failed (parent={ParentInstanceId}, child={ChildInstanceId}, definition={DefinitionKey}): {Reason}")]
    private partial void LogChildStartFailed(Guid parentInstanceId, Guid childInstanceId, string definitionKey, string reason);
}
