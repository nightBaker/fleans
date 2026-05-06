using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Effects;

public partial class TimerEffectHandler : IEffectHandler
{
    private readonly ILogger<TimerEffectHandler> _logger;

    public TimerEffectHandler(ILogger<TimerEffectHandler> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(IInfrastructureEffect effect) =>
        effect is RegisterTimerEffect or UnregisterTimerEffect;

    public async Task HandleAsync(IInfrastructureEffect effect, IEffectContext context)
    {
        switch (effect)
        {
            case RegisterTimerEffect timer:
                var callbackGrain = context.GrainFactory.GetGrain<ITimerCallbackGrain>(
                    timer.WorkflowInstanceId, $"{timer.HostActivityInstanceId}:{timer.TimerActivityId}");
                try
                {
                    await callbackGrain.Activate(timer.DueTime);
                    LogTimerReminderRegistered(timer.TimerActivityId, timer.DueTime);
                }
                catch (Exception ex)
                {
                    LogTimerReminderFailed(timer.TimerActivityId, ex);
                    await context.ProcessFailureEffects(timer.TimerActivityId, timer.HostActivityInstanceId, ex);
                }
                break;

            case UnregisterTimerEffect unregTimer:
                var timerCancelGrain = context.GrainFactory.GetGrain<ITimerCallbackGrain>(
                    unregTimer.WorkflowInstanceId, $"{unregTimer.HostActivityInstanceId}:{unregTimer.TimerActivityId}");
                try
                {
                    await timerCancelGrain.Cancel();
                    LogTimerReminderUnregistered(unregTimer.TimerActivityId);
                }
                catch (Exception ex)
                {
                    // Cleanup-path failure: the activity that owned this timer has already
                    // completed or been cancelled by the time we reach this handler, so failing
                    // the workflow now would violate the "Each activity instance executes at
                    // most once" invariant (CLAUDE.md Design Constraints). Log only.
                    LogTimerUnregisterFailed(unregTimer.TimerActivityId, ex);
                }
                break;

            default:
                throw new InvalidOperationException($"Unexpected effect type in {nameof(TimerEffectHandler)}: {effect.GetType().Name}");
        }
    }

    [LoggerMessage(EventId = 1017, Level = LogLevel.Information,
        Message = "Timer reminder registered for activity {TimerActivityId}, due in {DueTime}")]
    private partial void LogTimerReminderRegistered(string timerActivityId, TimeSpan dueTime);

    [LoggerMessage(EventId = 1018, Level = LogLevel.Warning,
        Message = "Timer reminder registration failed for activity {TimerActivityId}")]
    private partial void LogTimerReminderFailed(string timerActivityId, Exception exception);

    [LoggerMessage(EventId = 1019, Level = LogLevel.Information,
        Message = "Timer reminder unregistered for activity {TimerActivityId}")]
    private partial void LogTimerReminderUnregistered(string timerActivityId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Warning,
        Message = "Timer reminder unregister failed for activity {TimerActivityId} (cleanup-path; not failing workflow)")]
    private partial void LogTimerUnregisterFailed(string timerActivityId, Exception exception);
}
