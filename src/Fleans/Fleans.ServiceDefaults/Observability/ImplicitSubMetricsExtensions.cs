using Microsoft.Extensions.DependencyInjection;

namespace Fleans.ServiceDefaults.Observability;

/// <summary>
/// Registers <see cref="ImplicitSubMetricsHostedService"/> as a singleton hosted service.
/// Pulls <c>IConnectionMultiplexer</c> from DI if registered (production: Aspire/Redis
/// wires it; tests: not registered → service falls back to grain-count metric only).
/// </summary>
public static class ImplicitSubMetricsExtensions
{
    public static IServiceCollection AddImplicitSubMetrics(this IServiceCollection services)
    {
        services.AddHostedService<ImplicitSubMetricsHostedService>();
        return services;
    }
}
