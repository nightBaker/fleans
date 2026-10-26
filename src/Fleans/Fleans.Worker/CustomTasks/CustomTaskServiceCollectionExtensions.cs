using System.Reflection;
using Fleans.Application.Abstractions.Events;
using Fleans.Application.CustomTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Streams;

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
        // The task type becomes part of the grain type (CustomTaskGrainTypeProvider), and '/'
        // separates a GrainId's type from its key.
        if (taskType.Contains('/'))
            throw new ArgumentException($"TaskType '{taskType}' must not contain '/'.", nameof(taskType));

        // Validation 1: duplicate-TaskType check. Two plugins on the same TaskType would
        // both end up on the same per-type stream namespace and both fire on every event,
        // defeating the fanout-elimination goal of the per-type-namespace design.
        // Covers one host only: two hosts claiming one TaskType with different handlers are
        // not detected.
        // Note: relies on AddSingleton(new …) (instance-based registration) — the supported
        // entry point. Factory-form registrations would have null ImplementationInstance.
        var duplicate = services
            .Where(d => d.ServiceType == typeof(CustomTaskPluginDescriptor))
            .Select(d => d.ImplementationInstance as CustomTaskPluginDescriptor)
            .FirstOrDefault(d => d is not null && string.Equals(d.TaskType, taskType, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Custom-task plugin with TaskType '{taskType}' is already registered. " +
                $"Each TaskType may be claimed by at most one CustomTaskHandlerBase subclass.");
        }

        // Validation 2: attribute-presence + namespace-correctness on THandler. Orleans
        // walks [ImplicitStreamSubscription] only on concrete grain classes (inherit:false
        // matches that semantic), so a missing or drifted attribute means the plugin would
        // never receive its events at runtime. Fail loudly at silo start instead. The same
        // attribute names the handler's grain type (CustomTaskGrainTypeProvider).
        var expectedNs = WorkflowEventStreams.GetExecuteCustomTaskNamespace(taskType);
        var declaredNamespaces = CustomTaskGrainTypeProvider.SubscribedNamespaces(typeof(THandler)).ToList();
        if (!declaredNamespaces.Any(ns => string.Equals(ns, expectedNs, StringComparison.Ordinal)))
        {
            var found = declaredNamespaces.Count == 0
                ? "(none)"
                : string.Join(", ", declaredNamespaces.Select(ns => $"\"{ns}\""));
            throw new InvalidOperationException(
                $"{typeof(THandler).FullName} must declare " +
                $"[ImplicitStreamSubscription(\"{expectedNs}\")] " +
                $"(matching its TaskType \"{taskType}\"). Found attributes: [{found}].");
        }

        services.AddCustomTaskGrainTypes();
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

    /// <summary>
    /// Names each <see cref="CustomTaskHandlerBase"/> subclass's grain type after its task type
    /// (<c>custom-task.&lt;taskType&gt;</c>) instead of its class name, so plugins on different
    /// hosts never share a grain type. <see cref="AddCustomTaskPlugin{THandler}"/> and
    /// <c>AddFleansPluginHost</c> call it; idempotent.
    /// </summary>
    public static IServiceCollection AddCustomTaskGrainTypes(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGrainTypeProvider, CustomTaskGrainTypeProvider>());
        return services;
    }
}
