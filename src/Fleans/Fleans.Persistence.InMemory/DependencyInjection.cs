using Fleans.Domain.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Fleans.Persistence.InMemory;

public static class InMemoryPersistenceDependencyInjection
{
    /// <summary>
    /// Call after <c>UseOrleans</c> — registers named grain storage providers
    /// via keyed singletons (same mechanism Orleans uses internally).
    /// </summary>
    public static void AddInMemoryPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IProcessDefinitionRepository, InMemoryProcessDefinitionRepository>();
        services.AddSingleton<InMemoryGrainStorage>();
        services.AddKeyedSingleton<IGrainStorage>("workflowInstances",
            (sp, _) => sp.GetRequiredService<InMemoryGrainStorage>());
        services.AddKeyedSingleton<IGrainStorage>("activityInstances",
            (sp, _) => sp.GetRequiredService<InMemoryGrainStorage>());
    }
}
