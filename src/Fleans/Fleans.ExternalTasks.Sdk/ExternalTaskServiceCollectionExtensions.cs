using Fleans.Application.CustomTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Fleans.ExternalTasks.Sdk;

/// <summary>
/// Wires a worker process: <see cref="AddFleansExternalTaskWorker"/> once (the hosted gRPC worker),
/// then one <see cref="AddExternalTaskHandler{THandler}"/> per task type the process implements.
/// </summary>
public static class ExternalTaskServiceCollectionExtensions
{
    /// <summary>
    /// Adds the hosted worker that subscribes to the engine for every registered task type.
    /// Options bind from <c>Fleans:ExternalTaskWorker</c> (see <see cref="ExternalTaskWorkerOptions"/>);
    /// <paramref name="configure"/> runs after binding. Fails at startup without an
    /// <see cref="ExternalTaskWorkerOptions.EngineUrl"/>.
    /// </summary>
    public static IServiceCollection AddFleansExternalTaskWorker(
        this IServiceCollection services, Action<ExternalTaskWorkerOptions>? configure = null)
    {
        var options = services.AddOptions<ExternalTaskWorkerOptions>().BindConfiguration(ExternalTaskWorkerOptions.SectionName);
        if (configure is not null)
            options.Configure(configure);
        options
            .Validate(o => o.EngineUrl is { IsAbsoluteUri: true }, "Fleans:ExternalTaskWorker:EngineUrl must be an absolute URL of the Fleans API.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.WorkerName), "Fleans:ExternalTaskWorker:WorkerName must not be empty.")
            .Validate(o => o.MaxConcurrentTasks > 0, "Fleans:ExternalTaskWorker:MaxConcurrentTasks must be greater than 0.")
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ExternalTaskWorkerService>());
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="THandler"/> as the handler for <paramref name="taskType"/>
    /// (the BPMN <c>type="…"</c>). <paramref name="parameterSchema"/> is what the management UI's
    /// BPMN editor renders a typed editor from; pass <see cref="CustomTaskParameterSchema.Empty"/>
    /// when the handler takes no inputs, or omit it to leave the task editor-opaque (free-form
    /// key/value inputs). Throws when <paramref name="taskType"/> is already registered.
    /// </summary>
    public static IServiceCollection AddExternalTaskHandler<THandler>(
        this IServiceCollection services,
        string taskType,
        string? displayName = null,
        CustomTaskParameterSchema? parameterSchema = null)
        where THandler : ExternalTaskHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);

        // Relies on AddSingleton(new …) (instance-based registration) — the supported entry point.
        var duplicate = services
            .Select(d => d.ImplementationInstance as ExternalTaskHandlerDescriptor)
            .FirstOrDefault(d => d is not null && string.Equals(d.TaskType, taskType, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"External-task handler for TaskType '{taskType}' is already registered ({duplicate.HandlerType.FullName}). " +
                "Each TaskType may be claimed by at most one ExternalTaskHandler.");
        }

        services.TryAddTransient<THandler>();
        services.AddSingleton(new ExternalTaskHandlerDescriptor(taskType, displayName, parameterSchema, typeof(THandler)));
        return services;
    }
}

/// <summary>One registered handler: what the worker announces to the engine, and its handler.</summary>
internal sealed record ExternalTaskHandlerDescriptor(
    string TaskType,
    string? DisplayName,
    CustomTaskParameterSchema? ParameterSchema,
    Type HandlerType);
