using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Effects;

public partial class EffectDispatcher
{
    private readonly IReadOnlyList<IEffectHandler> _handlers;
    private readonly ILogger<EffectDispatcher> _logger;

    public EffectDispatcher(IEnumerable<IEffectHandler> handlers, ILogger<EffectDispatcher> logger)
    {
        _handlers = handlers.ToList();
        _logger = logger;

        // Validate no duplicate handler registrations at startup
        var duplicates = _handlers
            .GroupBy(h => h.GetType())
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Name)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate effect handler registrations: {string.Join(", ", duplicates)}");
        }
    }

    public async Task DispatchAsync(IReadOnlyList<IInfrastructureEffect> effects, IEffectContext context)
    {
        foreach (var effect in effects)
        {
            if (SkipReason(effect, context) is { } reason)
            {
                LogEffectSkipped(effect.GetType().Name, reason);
                continue;
            }

            var matching = _handlers.Where(h => h.CanHandle(effect)).ToList();
            if (matching.Count == 0)
                throw new InvalidOperationException(
                    $"No handler registered for effect type: {effect.GetType().Name}");

            if (matching.Count > 1)
                LogMultipleHandlers(
                    effect.GetType().Name,
                    string.Join(", ", matching.Select(h => h.GetType().Name)));

            await matching[0].HandleAsync(effect, context);
        }
    }

    private static string? SkipReason(IInfrastructureEffect effect, IEffectContext context) => effect switch
    {
        UnregisterTimerEffect u when context.FiredTimer is var (timerId, hostId)
            && u.TimerActivityId == timerId && u.HostActivityInstanceId == hostId
            => "the fired timer's callback grain removes its own reminder",
        RegisterTimerEffect or SubscribeMessageEffect or SubscribeSignalEffect when context.IsInstanceTerminal
            => "the instance is already terminal",
        _ => null,
    };

    [LoggerMessage(EventId = 1251, Level = LogLevel.Debug,
        Message = "Skipped {EffectType}: {Reason}")]
    private partial void LogEffectSkipped(string effectType, string reason);

    [LoggerMessage(EventId = 1250, Level = LogLevel.Warning,
        Message = "Multiple handlers can handle effect type {EffectType}: {Handlers}. Using first match.")]
    private partial void LogMultipleHandlers(string effectType, string handlers);
}
