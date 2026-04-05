using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Effects;

public partial class SignalEffectHandler : IEffectHandler
{
    private readonly ILogger<SignalEffectHandler> _logger;

    public SignalEffectHandler(ILogger<SignalEffectHandler> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(IInfrastructureEffect effect) =>
        effect is SubscribeSignalEffect or UnsubscribeSignalEffect or ThrowSignalEffect;

    public async Task HandleAsync(IInfrastructureEffect effect, IEffectContext context)
    {
        switch (effect)
        {
            case SubscribeSignalEffect subSig:
                await PerformSignalSubscribe(subSig, context);
                break;

            case UnsubscribeSignalEffect unsubSig:
                var unsubSigGrain = context.GrainFactory.GetGrain<ISignalCorrelationGrain>(unsubSig.SignalName);
                await unsubSigGrain.Unsubscribe(unsubSig.WorkflowInstanceId, unsubSig.ActivityId);
                break;

            case ThrowSignalEffect throwSig:
                var throwSigGrain = context.GrainFactory.GetGrain<ISignalCorrelationGrain>(throwSig.SignalName);
                var deliveredCount = await throwSigGrain.BroadcastSignal();
                LogSignalThrown(throwSig.SignalName, deliveredCount);
                break;

            default:
                throw new InvalidOperationException($"Unexpected effect type in {nameof(SignalEffectHandler)}: {effect.GetType().Name}");
        }
    }

    private async Task PerformSignalSubscribe(SubscribeSignalEffect subSig, IEffectContext context)
    {
        var signalGrain = context.GrainFactory.GetGrain<ISignalCorrelationGrain>(subSig.SignalName);

        try
        {
            await context.PersistStateAsync(); // persist before external call
            await signalGrain.Subscribe(subSig.WorkflowInstanceId, subSig.ActivityId, subSig.HostActivityInstanceId);
            LogSignalSubscriptionRegistered(subSig.ActivityId, subSig.SignalName);
        }
        catch (Exception ex)
        {
            LogSignalSubscriptionFailed(subSig.ActivityId, subSig.SignalName, ex);
            await context.ProcessFailureEffects(subSig.ActivityId, subSig.HostActivityInstanceId, ex);
        }
    }

    [LoggerMessage(EventId = 1028, Level = LogLevel.Information,
        Message = "Signal subscription registered for activity {ActivityId}: signalName={SignalName}")]
    private partial void LogSignalSubscriptionRegistered(string activityId, string signalName);

    [LoggerMessage(EventId = 1029, Level = LogLevel.Warning,
        Message = "Signal subscription failed for activity {ActivityId}: signalName={SignalName}")]
    private partial void LogSignalSubscriptionFailed(string activityId, string signalName, Exception exception);

    [LoggerMessage(EventId = 1030, Level = LogLevel.Information,
        Message = "Signal thrown: signalName={SignalName}, deliveredTo={DeliveredCount} subscribers")]
    private partial void LogSignalThrown(string signalName, int deliveredCount);
}
