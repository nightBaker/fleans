using Fleans.Domain.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Fleans.Persistence.InMemory;

public static class InMemoryPersistenceDependencyInjection
{
    public static void AddInMemoryPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IProcessDefinitionRepository, InMemoryProcessDefinitionRepository>();
        services.AddKeyedSingleton<IGrainStorage>("workflowInstances", (_, _) => new WorkflowInstanceGrainStorage());
        services.AddKeyedSingleton<IGrainStorage>("activityInstances", (_, _) => new ActivityInstanceGrainStorage());
    }
}
