using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Fleans.ExternalTasks.Sdk;

/// <summary>
/// Wires a worker process: <see cref="AddFleansExternalTaskWorker"/> once (the hosted gRPC worker),
/// then one <see cref="AddExternalTaskHandler{THandler}"/> per task type the process implements.
/// </summary>
public static partial class ExternalTaskServiceCollectionExtensions
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
    /// (the BPMN <c>type="…"</c>), or <c>"&lt;scheme&gt;:*"</c> for every type starting with
    /// <c>"&lt;scheme&gt;:"</c> that no worker claims exactly (a wildcard is not listed in the catalog).
    /// <paramref name="parameterSchema"/> is what the management UI's
    /// BPMN editor renders a typed editor from; pass <see cref="ExternalTaskParameterSchema.Empty"/>
    /// when the handler takes no inputs, or omit it to leave the task editor-opaque (free-form
    /// key/value inputs). <paramref name="category"/> groups the task in the editor's picker. A
    /// <see cref="ExternalTaskVisibility.Private"/> task can only be deployed in the processes on its
    /// allow-list; <paramref name="allowedProcessKeys"/> is that list until it is first edited through
    /// the API or the UI. <paramref name="variables"/> says which workflow variables the handler
    /// receives; omit it for <see cref="ExternalTaskVariables.None"/>. <paramref name="version"/> is the
    /// plugin's semantic version, which processes can pin; bump it whenever
    /// <paramref name="parameterSchema"/> changes, since the engine refuses a worker declaring different
    /// parameters from a connected worker of the same version. Throws when <paramref name="taskType"/>
    /// is already registered or the registration declares something the engine would refuse.
    /// </summary>
    public static IServiceCollection AddExternalTaskHandler<THandler>(
        this IServiceCollection services,
        string taskType,
        string? displayName = null,
        ExternalTaskParameterSchema? parameterSchema = null,
        string? category = null,
        ExternalTaskVisibility visibility = ExternalTaskVisibility.Public,
        IReadOnlyList<string>? allowedProcessKeys = null,
        ExternalTaskVariables? variables = null,
        string? version = null)
        where THandler : ExternalTaskHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        Validate(taskType, category, visibility, allowedProcessKeys, version);

        // Relies on AddSingleton(new …) (instance-based registration) — the supported entry point.
        var duplicate = services
            .Where(d => d.ServiceType == typeof(ExternalTaskHandlerDescriptor))
            .Select(d => d.ImplementationInstance as ExternalTaskHandlerDescriptor)
            .FirstOrDefault(d => d is not null && string.Equals(d.TaskType, taskType, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"External-task handler for TaskType '{taskType}' is already registered ({duplicate.HandlerType.FullName}). " +
                "Each TaskType may be claimed by at most one ExternalTaskHandler.");
        }

        services.TryAddTransient<THandler>();
        services.AddSingleton(new ExternalTaskHandlerDescriptor(taskType, displayName, parameterSchema, typeof(THandler), category, visibility, allowedProcessKeys, variables ?? ExternalTaskVariables.None, version));
        return services;
    }

    // The engine refuses these too, but a refused subscription only retries forever: fail at startup.
    private static void Validate(
        string taskType, string? category, ExternalTaskVisibility visibility, IReadOnlyList<string>? allowedProcessKeys,
        string? version)
    {
        var star = taskType.IndexOf('*');
        if (star >= 0 && (star != taskType.Length - 1 || taskType.Length < 3 || taskType[star - 1] != ':'))
            throw new ArgumentException($"TaskType '{taskType}': a wildcard must have the form '<scheme>:*'.", nameof(taskType));
        if (category?.Length > 128)
            throw new ArgumentException($"The category of '{taskType}' is longer than 128 characters.", nameof(category));
        if (allowedProcessKeys?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException($"A process key in the allow-list of '{taskType}' is blank.", nameof(allowedProcessKeys));
        if (visibility == ExternalTaskVisibility.Public && allowedProcessKeys is { Count: > 0 })
            throw new ArgumentException($"'{taskType}' is public, so it cannot declare allowed processes.", nameof(allowedProcessKeys));
        if (version is not null && (version.Length > 128 || !SemanticVersion().IsMatch(version)))
            throw new ArgumentException($"The version of '{taskType}', '{version}', is not a semantic version such as 1.4.0.", nameof(version));
    }

    [GeneratedRegex(@"^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(-(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*)?(\+[0-9a-zA-Z-]+(\.[0-9a-zA-Z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersion();
}

/// <summary>One registered handler: what the worker announces to the engine, and its handler.</summary>
internal sealed record ExternalTaskHandlerDescriptor(
    string TaskType,
    string? DisplayName,
    ExternalTaskParameterSchema? ParameterSchema,
    Type HandlerType,
    string? Category,
    ExternalTaskVisibility Visibility,
    IReadOnlyList<string>? AllowedProcessKeys,
    ExternalTaskVariables Variables,
    string? Version);
