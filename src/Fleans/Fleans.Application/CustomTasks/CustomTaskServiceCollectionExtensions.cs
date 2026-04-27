using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fleans.Application.CustomTasks;

public static class CustomTaskServiceCollectionExtensions
{
    /// <summary>
    /// Registers a per-plugin grain interface as the implementation for a given <c>taskType</c>
    /// discriminator on <c>&lt;serviceTask type="..."&gt;</c>. The registry resolves the grain
    /// interface at activity-execution time and Orleans hands back a grain reference of the
    /// declared interface type.
    /// </summary>
    /// <remarks>
    /// All <see cref="AddCustomTaskProvider{TGrainInterface}"/> calls must complete before the first
    /// resolution of <see cref="CustomTaskCallProviderRegistry"/>. In production this is the host
    /// startup; in tests, register all providers before resolving the registry.
    /// </remarks>
    public static IServiceCollection AddCustomTaskProvider<TGrainInterface>(
        this IServiceCollection services,
        string taskType)
        where TGrainInterface : ICustomTaskCallProvider
    {
        if (string.IsNullOrWhiteSpace(taskType))
            throw new ArgumentException("Task type must be non-empty", nameof(taskType));

        services.AddSingleton(new CustomTaskRegistration(taskType, typeof(TGrainInterface)));
        services.TryAddSingleton<CustomTaskCallProviderRegistry>();
        return services;
    }
}
