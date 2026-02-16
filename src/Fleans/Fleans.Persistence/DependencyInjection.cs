using Fleans.Application;
using Fleans.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Fleans.Persistence;

public static class EfCorePersistenceDependencyInjection
{
    public static void AddEfCorePersistence(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureCommandDb,
        Action<DbContextOptionsBuilder>? configureQueryDb = null)
    {
        services.AddDbContextFactory<FleanCommandDbContext>(configureCommandDb);
        services.AddDbContextFactory<FleanQueryDbContext>(configureQueryDb ?? configureCommandDb);

        services.AddKeyedSingleton<IGrainStorage>("activityInstances",
            (sp, _) => new EfCoreActivityInstanceGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>("workflowInstances",
            (sp, _) => new EfCoreWorkflowInstanceGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddSingleton<IProcessDefinitionRepository, EfCoreProcessDefinitionRepository>();
        services.AddSingleton<IWorkflowQueryService, WorkflowQueryService>();
    }
}
