using Fleans.Application.ExternalTasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Infrastructure.ExternalTasks;

public static class ExternalTaskDependencyInjection
{
    /// <summary>
    /// Registers the external-task queue named by <c>Fleans:ExternalTasks:Provider</c> (#979): <c>kafka</c>,
    /// or nothing when unset. Called on silos that host workflow instances. To add a provider, implement
    /// <see cref="IExternalTaskQueue"/> and add a case here.
    /// </summary>
    public static IServiceCollection AddExternalTasks(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration[$"{ExternalTaskOptions.SectionName}:Provider"];
        switch (provider?.ToLowerInvariant())
        {
            case null or "":
                break;
            case "kafka":
                services.AddOptions<KafkaExternalTaskOptions>().BindConfiguration(KafkaExternalTaskOptions.SectionName);
                services.AddSingleton<KafkaExternalTaskQueue>();
                services.AddSingleton<IExternalTaskQueue>(sp => sp.GetRequiredService<KafkaExternalTaskQueue>());
                services.AddHostedService(sp => sp.GetRequiredService<KafkaExternalTaskQueue>());
                break;
            default:
                throw new ArgumentException(
                    $"Unknown external-task provider '{provider}'. Supported: kafka. " +
                    $"To add a provider, implement {nameof(IExternalTaskQueue)} and add a case to {nameof(ExternalTaskDependencyInjection)}.{nameof(AddExternalTasks)}.");
        }
        return services;
    }
}
