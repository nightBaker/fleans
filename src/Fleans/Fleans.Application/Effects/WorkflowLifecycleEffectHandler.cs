using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Effects;
using Fleans.Domain.Errors;
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
        effect is StartChildWorkflowEffect or CancelChildWorkflowEffect or NotifyParentCompletedEffect
            or NotifyParentFailedEffect or NotifyParentEscalationRaisedEffect
            or PublishDomainEventEffect;

    public async Task HandleAsync(IInfrastructureEffect effect, IEffectContext context)
    {
        switch (effect)
        {
            case StartChildWorkflowEffect startChild:
                await PerformStartChildWorkflow(startChild, context);
                break;

            case CancelChildWorkflowEffect cancelChild:
                // Cleanup: the parent has already moved on, so a failure here is logged only.
                // One-way, so a child that is calling back into this parent cannot deadlock it.
                try
                {
                    LogCancellingChildWorkflow(cancelChild.ChildInstanceId);
                    await context.GrainFactory.GetGrain<IWorkflowInstanceGrain>(cancelChild.ChildInstanceId)
                        .CancelFromParent(cancelChild.Reason);
                }
                catch (Exception ex)
                {
                    LogChildCancelFailed(context.WorkflowInstanceId, cancelChild.ChildInstanceId, ex.Message);
                }
                break;

            case NotifyParentCompletedEffect notifyCompleted:
                // Op-id is deterministic per logical call so a retried RPC dedups (#657).
                var completedOpId = GrainFactoryRetryExtensions.ChildCompletedOpId(
                    context.WorkflowInstanceId, notifyCompleted.ParentActivityId);
                await context.GrainFactory.CallWithRetry<IWorkflowInstanceGrain>(
                    notifyCompleted.ParentInstanceId, completedOpId,
                    g => g.OnChildWorkflowCompleted(completedOpId, context.WorkflowInstanceId, notifyCompleted.ParentActivityId, notifyCompleted.Variables),
                    GrainFactoryRetryExtensions.ParentNotificationAttempts, retryTimeouts: true);
                break;

            case NotifyParentFailedEffect notifyFailed:
                var failedOpId = GrainFactoryRetryExtensions.ChildFailedOpId(
                    context.WorkflowInstanceId, notifyFailed.ParentActivityId);
                await context.GrainFactory.CallWithRetry<IWorkflowInstanceGrain>(
                    notifyFailed.ParentInstanceId, failedOpId,
                    g => g.OnChildWorkflowFailed(failedOpId, context.WorkflowInstanceId, notifyFailed.ParentActivityId,
                        ToSerializableFailure(notifyFailed.Exception)),
                    GrainFactoryRetryExtensions.ParentNotificationAttempts, retryTimeouts: true);
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
                        escalation.EscalationCode, escalation.Variables),
                    GrainFactoryRetryExtensions.ParentNotificationAttempts, retryTimeouts: true);
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
            // A disabled called process fails the call activity, like any other child start failure
            // (catchable by an error boundary): a disabled process starts no new instance (#863).
            var childDefinition = await processGrain.GetStartableDefinition()
                ?? throw new ProcessDisabledException(startChild.ProcessDefinitionKey);

            LogStartingChildWorkflow(startChild.ProcessDefinitionKey, startChild.ChildInstanceId);

            await context.GrainFactory.GetGrain<IWorkflowInstanceGrain>(startChild.ChildInstanceId)
                .StartWorkflow(childDefinition,
                    variables: startChild.InputVariables,
                    parent: new ParentInfo(context.WorkflowInstanceId, startChild.ParentActivityId));
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

    /// <summary>
    /// The exception crosses to the parent, possibly on another silo. Any exception type may fail
    /// the child (e.g. the domain's private mapping exception), and one without an Orleans codec
    /// arrives as "Could not find a codec…" instead of its message. An <see cref="ActivityException"/>
    /// is serializable and carries the error code boundaries match on; anything else is sent as a
    /// plain <see cref="Exception"/> with its message — all the parent keeps of it (code 500).
    /// </summary>
    private static Exception ToSerializableFailure(Exception exception) =>
        exception is ActivityException ? exception : new Exception(exception.Message);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Information,
        Message = "Starting child workflow: CalledProcessKey={CalledProcessKey}, ChildId={ChildId}")]
    private partial void LogStartingChildWorkflow(string calledProcessKey, Guid childId);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Error,
        Message = "Child workflow start failed (parent={ParentInstanceId}, child={ChildInstanceId}, definition={DefinitionKey}): {Reason}")]
    private partial void LogChildStartFailed(Guid parentInstanceId, Guid childInstanceId, string definitionKey, string reason);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
        Message = "Cancelling child workflow {ChildId}: its call activity was cancelled")]
    private partial void LogCancellingChildWorkflow(Guid childId);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning,
        Message = "Child workflow cancel failed (parent={ParentInstanceId}, child={ChildInstanceId}): {Reason}")]
    private partial void LogChildCancelFailed(Guid parentInstanceId, Guid childInstanceId, string reason);
}
