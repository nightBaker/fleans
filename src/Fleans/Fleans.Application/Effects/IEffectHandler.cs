using Fleans.Domain.Effects;

namespace Fleans.Application.Effects;

public interface IEffectHandler
{
    bool CanHandle(IInfrastructureEffect effect);
    Task HandleAsync(IInfrastructureEffect effect, IEffectContext context);
}
