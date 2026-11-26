using Fleans.Application.Grains;
using Fleans.Application.StartEvents;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleans.Application.Effects;

public partial class SignalEffectHandler : IEffectHandler
{
    private readonly ILogger<SignalEffectHandler> _logger;
    private readonly SignalCorrelationOptions _signals;
    private readonly StartEventLauncher? _startEvents;

    public SignalEffectHandler(ILogger<SignalEffectHandler> logger, IOptions<SignalCorrelationOptions> signals,
        StartEventLauncher? startEvents = null)
    {
        _logger = logger;
        _signals = signals.Value;
        _startEvents = startEvents;
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
                    await unsubSigGrain.Unsubscribe(unsubSig.WorkflowInstanceId, unsubSig.ActivityId, unsubSig.HostActivityInstanceId);
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
                StartSignalStartEventsInBackground(throwSig.SignalName);
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

    /// <summary>
    /// A thrown signal also starts the processes with a signal start event for it, as a REST
    /// <c>SendSignal</c> does (#853). Not awaited: the throwing instance's turn must not wait on
    /// the starts, and a started instance that throws the same signal must not wait on this one.
    /// Best-effort like the broadcast: a failure is logged.
    /// </summary>
    private void StartSignalStartEventsInBackground(string signalName)
    {
        if (_startEvents is not { } startEvents) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var started = await startEvents.StartBySignal(signalName);
                if (started.Count > 0)
                    LogSignalStartEventsFired(signalName, started.Count);
            }
            catch (Exception ex)
            {
                LogSignalStartEventsFailed(signalName, ex);
            }
        });
    }

    [LoggerMessage(EventId = 1235, Level = LogLevel.Information,
        Message = "Thrown signal started {StartedCount} instances through signal start events: signalName={SignalName}")]
    private partial void LogSignalStartEventsFired(string signalName, int startedCount);

    [LoggerMessage(EventId = 1236, Level = LogLevel.Warning,
        Message = "Thrown signal could not start signal start events: signalName={SignalName} (best-effort; not failing workflow)")]
    private partial void LogSignalStartEventsFailed(string signalName, Exception exception);

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
