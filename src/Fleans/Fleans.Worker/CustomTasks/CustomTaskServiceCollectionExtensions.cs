using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;

namespace Fleans.Worker.CustomTasks;

/// <summary>
/// Plugin authors call <see cref="AddCustomTaskPlugin{THandler}"/> from their
/// <c>services.AddXxxPlugin(...)</c> extension to (a) bind the handler grain class
/// into the silo (Orleans discovers it automatically via assembly scanning), and
/// (b) register the plugin's metadata so <see cref="CustomTaskPluginRegistrar"/>
/// announces it to the catalog at silo startup.
/// </summary>
public static class CustomTaskServiceCollectionExtensions
{
    public static IServiceCollection AddCustomTaskPlugin<THandler>(
        this IServiceCollection services,
        string taskType,
        string? displayName = null,
        string? parameterSchemaJson = null)
        where THandler : CustomTaskHandlerBase
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);

        services.AddSingleton(new CustomTaskPluginDescriptor(taskType, displayName, parameterSchemaJson));

        // Register the lifecycle participant exactly once even if multiple plugins are added.
        services.TryAddSingleton<CustomTaskPluginRegistrar>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILifecycleParticipant<ISiloLifecycle>>(
            sp => sp.GetRequiredService<CustomTaskPluginRegistrar>()));

        return services;
    }
}
