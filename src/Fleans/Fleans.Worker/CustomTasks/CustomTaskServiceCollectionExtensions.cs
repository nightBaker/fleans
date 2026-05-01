using Fleans.Application.CustomTasks;
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
///
/// The <paramref name="parameterSchema"/> is what the management UI's BPMN editor
/// (sub-issue C) uses to render a typed editor for each <c>&lt;zeebe:input&gt;</c>
/// the plugin expects. Pass <see cref="CustomTaskParameterSchema.Empty"/> when the
/// plugin takes no inputs; omit the argument when the plugin should remain
/// editor-opaque (UI falls back to a free-form key/value editor).
/// </summary>
public static class CustomTaskServiceCollectionExtensions
{
    public static IServiceCollection AddCustomTaskPlugin<THandler>(
        this IServiceCollection services,
        string taskType,
        string? displayName = null,
        CustomTaskParameterSchema? parameterSchema = null)
        where THandler : CustomTaskHandlerBase
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);

        services.AddSingleton(new CustomTaskPluginDescriptor(taskType, displayName, parameterSchema));

        // Register the registrar singleton + its ILifecycleParticipant facet exactly once,
        // regardless of how many plugins are added. We can't use TryAddEnumerable for the
        // facet — the factory-form ServiceDescriptor has no ImplementationType, so
        // TryAddEnumerable's dedupe check throws "Implementation type … is indistinguishable
        // from other services" the moment a second AddCustomTaskPlugin call lands. Manual
        // check on the descriptor list is unambiguous and lets the lifecycle participant
        // share the same instance as the singleton.
        if (services.All(d => d.ServiceType != typeof(CustomTaskPluginRegistrar)))
        {
            services.AddSingleton<CustomTaskPluginRegistrar>();
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(
                sp => sp.GetRequiredService<CustomTaskPluginRegistrar>());
        }

        return services;
    }
}
