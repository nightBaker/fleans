using System.Reflection;
using Fleans.Application.Abstractions.Events;
using Fleans.Application.CustomTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Validation 1: duplicate-TaskType check. Two plugins on the same TaskType would
        // both end up on the same per-type stream namespace and both fire on every event,
        // defeating the fanout-elimination goal of the per-type-namespace design.
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
        // never receive its events at runtime. Fail loudly at silo start instead.
        //
        // ImplicitStreamSubscriptionAttribute exposes its namespace string via the
        // predicate-form constructor only; the literal-string overload stores it on a
        // private field surfaced as an IStreamNamespacePredicate. CustomAttributeData
        // gives direct access to the original constructor argument — that's what Orleans
        // itself reads during grain-class discovery, so matching on it is byte-equivalent.
        var expectedNs = WorkflowEventStreams.GetExecuteCustomTaskNamespace(taskType);
        var declaredNamespaces = typeof(THandler)
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(ImplicitStreamSubscriptionAttribute))
            .Select(a => a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null)
            .Where(s => s is not null)
            .Cast<string>()
            .ToList();
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
