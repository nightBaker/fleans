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
                await callbackGrain.Activate(timer.DueTime);
                LogTimerReminderRegistered(timer.TimerActivityId, timer.DueTime);
                break;

            case UnregisterTimerEffect unregTimer:
                var timerCancelGrain = context.GrainFactory.GetGrain<ITimerCallbackGrain>(
                    unregTimer.WorkflowInstanceId, $"{unregTimer.HostActivityInstanceId}:{unregTimer.TimerActivityId}");
                await timerCancelGrain.Cancel();
                LogTimerReminderUnregistered(unregTimer.TimerActivityId);
                break;

            default:
                throw new InvalidOperationException($"Unexpected effect type in {nameof(TimerEffectHandler)}: {effect.GetType().Name}");
        }
    }

    [LoggerMessage(EventId = 1017, Level = LogLevel.Information,
        Message = "Timer reminder registered for activity {TimerActivityId}, due in {DueTime}")]
    private partial void LogTimerReminderRegistered(string timerActivityId, TimeSpan dueTime);

    [LoggerMessage(EventId = 1019, Level = LogLevel.Information,
        Message = "Timer reminder unregistered for activity {TimerActivityId}")]
    private partial void LogTimerReminderUnregistered(string timerActivityId);
}
