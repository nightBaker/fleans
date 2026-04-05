using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Effects;

public class EffectDispatcher
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
            var matching = _handlers.Where(h => h.CanHandle(effect)).ToList();
            if (matching.Count == 0)
                throw new InvalidOperationException(
                    $"No handler registered for effect type: {effect.GetType().Name}");

            if (matching.Count > 1)
                _logger.LogWarning(
                    "Multiple handlers can handle effect type {EffectType}: {Handlers}. Using first match.",
                    effect.GetType().Name,
                    string.Join(", ", matching.Select(h => h.GetType().Name)));

            await matching[0].HandleAsync(effect, context);
        }
    }
}
