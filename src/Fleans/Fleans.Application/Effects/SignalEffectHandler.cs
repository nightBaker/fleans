using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleans.Application.Effects;

public partial class SignalEffectHandler : IEffectHandler
{
    private readonly ILogger<SignalEffectHandler> _logger;
    private readonly SignalCorrelationOptions _signals;

    public SignalEffectHandler(ILogger<SignalEffectHandler> logger, IOptions<SignalCorrelationOptions> signals)
    {
        _logger = logger;
        _signals = signals.Value;
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
                var unsubSigGrain = context.GrainFactory.GetSignalCorrelationGrain(
                    unsubSig.SignalName, unsubSig.WorkflowInstanceId, _signals);
                try
                {
                    await unsubSigGrain.Unsubscribe(unsubSig.WorkflowInstanceId, unsubSig.ActivityId);
                }
                catch (Exception ex)
                {
                    // Cleanup-path failure: the activity that owned this signal subscription
                    // has already completed or been cancelled by the time we reach this
                    // handler, so failing the workflow now would violate the "Each activity
                    // instance executes at most once" invariant (CLAUDE.md Design Constraints).
                    // Log only.
                    LogSignalUnsubscribeFailed(unsubSig.ActivityId, unsubSig.SignalName, ex);
                }
                break;

            case ThrowSignalEffect throwSig:
                // Side-effect failure: signal broadcast is best-effort. The activity that
                // emitted the throw has already transitioned past its "throw" step by the
                // time we dispatch this effect; failing the workflow on a missed broadcast
                // delivery doesn't sensibly attribute back to a single activity. Log only —
                // BroadcastSignal never throws; a failed bucket is reported in its result.
                var broadcast = await context.GrainFactory.BroadcastSignal(throwSig.SignalName, _signals);
                if (broadcast.Failure is not null)
                    LogSignalThrowFailed(throwSig.SignalName, broadcast.FailedBucketCount, broadcast.Failure);
                LogSignalThrown(throwSig.SignalName, broadcast.DeliveredCount);
                break;

            default:
                throw new InvalidOperationException($"Unexpected effect type in {nameof(SignalEffectHandler)}: {effect.GetType().Name}");
        }
    }

    private async Task PerformSignalSubscribe(SubscribeSignalEffect subSig, IEffectContext context)
    {
        var signalGrain = context.GrainFactory.GetSignalCorrelationGrain(
            subSig.SignalName, subSig.WorkflowInstanceId, _signals);

        try
        {
            await context.PersistStateAsync(); // persist before external call
            await signalGrain.Subscribe(subSig.WorkflowInstanceId, subSig.ActivityId, subSig.HostActivityInstanceId);
            LogSignalSubscriptionRegistered(subSig.ActivityId, subSig.SignalName);
        }
        // The persist found the instance retiring (#845): not a registration failure — the turn
        // stops and the caller's retry reaches the next activation.
        catch (Exception ex) when (ex is not WorkflowInstanceStaleException)
        {
            LogSignalSubscriptionFailed(subSig.ActivityId, subSig.SignalName, ex);
            await context.ProcessFailureEffects(subSig.ActivityId, subSig.HostActivityInstanceId, ex);
        }
    }

    [LoggerMessage(EventId = 1230, Level = LogLevel.Information,
        Message = "Signal subscription registered for activity {ActivityId}: signalName={SignalName}")]
    private partial void LogSignalSubscriptionRegistered(string activityId, string signalName);

    [LoggerMessage(EventId = 1231, Level = LogLevel.Warning,
        Message = "Signal subscription failed for activity {ActivityId}: signalName={SignalName}")]
    private partial void LogSignalSubscriptionFailed(string activityId, string signalName, Exception exception);

    [LoggerMessage(EventId = 1232, Level = LogLevel.Information,
        Message = "Signal thrown: signalName={SignalName}, deliveredTo={DeliveredCount} subscribers")]
    private partial void LogSignalThrown(string signalName, int deliveredCount);

    [LoggerMessage(EventId = 1233, Level = LogLevel.Warning,
        Message = "Signal unsubscribe failed for activity {ActivityId}: signalName={SignalName} (cleanup-path; not failing workflow)")]
    private partial void LogSignalUnsubscribeFailed(string activityId, string signalName, Exception exception);

    [LoggerMessage(EventId = 1234, Level = LogLevel.Warning,
        Message = "Signal throw broadcast failed in {FailedBucketCount} buckets: signalName={SignalName} (best-effort; not failing workflow)")]
    private partial void LogSignalThrowFailed(string signalName, int failedBucketCount, Exception exception);
}
