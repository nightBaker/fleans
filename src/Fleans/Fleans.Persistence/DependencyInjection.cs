using Fleans.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Fleans.Persistence;

public static class EfCorePersistenceDependencyInjection
{
    public static void AddEfCorePersistence(this IServiceCollection services, Action<DbContextOptionsBuilder> configureDb)
    {
        services.AddDbContextFactory<FleanDbContext>(configureDb);

        services.AddKeyedSingleton<IGrainStorage>("activityInstances",
            (sp, _) => new EfCoreActivityInstanceGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>("workflowInstances",
            (sp, _) => new EfCoreWorkflowInstanceGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanDbContext>>()));

        services.AddSingleton<IProcessDefinitionRepository, EfCoreProcessDefinitionRepository>();
    }
}
