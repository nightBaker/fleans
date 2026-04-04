using Fleans.Domain.Effects;

namespace Fleans.Application.Effects;

public class EffectDispatcher
{
    private readonly IReadOnlyList<IEffectHandler> _handlers;

    public EffectDispatcher(IEnumerable<IEffectHandler> handlers)
    {
        _handlers = handlers.ToList();
    }

    public async Task DispatchAsync(IReadOnlyList<IInfrastructureEffect> effects, IEffectContext context)
    {
        foreach (var effect in effects)
        {
            var handler = _handlers.FirstOrDefault(h => h.CanHandle(effect))
                ?? throw new InvalidOperationException(
                    $"No handler registered for effect type: {effect.GetType().Name}");
            await handler.HandleAsync(effect, context);
        }
    }
}
