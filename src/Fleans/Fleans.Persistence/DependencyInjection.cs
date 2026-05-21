using Fleans.Application;
using Fleans.Domain;
using Fleans.Domain.Events;
using Fleans.Domain.Persistence;
using Fleans.Persistence.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using Sieve.Models;
using Sieve.Services;

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

        // CQRS read-side wrapper (see #661): wraps the EF Core factory in an
        // IFleanQueryContextFactory that returns IQueryable<T>-only views. Singleton lifetime
        // matches the underlying EF Core factory; the per-call IFleanQueryContext is disposed
        // inside the consumer's `await using` block.
        services.AddSingleton<IFleanQueryContextFactory, FleanQueryContextFactory>();

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.ProcessDefinitions,
            (sp, _) => new EfCoreProcessDefinitionGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.TimerSchedulers,
            (sp, _) => new EfCoreTimerSchedulerGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.MessageCorrelations,
            (sp, _) => new EfCoreMessageCorrelationGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.SignalCorrelations,
            (sp, _) => new EfCoreSignalCorrelationGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.MessageStartEventListeners,
            (sp, _) => new EfCoreMessageStartEventListenerGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.SignalStartEventListeners,
            (sp, _) => new EfCoreSignalStartEventListenerGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.UserTasks,
            (sp, _) => new EfCoreUserTaskGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.ConditionalStartEventListeners,
            (sp, _) => new EfCoreConditionalStartEventListenerGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.ConditionalStartEventRegistry,
            (sp, _) => new EfCoreConditionalStartEventRegistryGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.CustomTaskCatalog,
            (sp, _) => new EfCoreCustomTaskCatalogGrainStorage(
                sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

        services.AddSingleton<IProcessDefinitionRepository, EfCoreProcessDefinitionRepository>();
        services.AddSingleton<ISieveProcessor, ApplicationSieveProcessor>();
        services.Configure<SieveOptions>(options =>
        {
            options.DefaultPageSize = 20;
            options.MaxPageSize = 100;
        });
        // Default in-memory user-task filter strategy; provider-specific extensions
        // (e.g. AddPostgresPersistence) override this with a SQL-pushdown impl. See #415.
        services.AddSingleton<IUserTaskFilterStrategy, InMemoryUserTaskFilterStrategy>();
        services.AddSingleton<IWorkflowQueryService, WorkflowQueryService>();
        services.AddSingleton<IWorkflowStateProjection, EfCoreWorkflowStateProjection>();
        services.AddSingleton<EfCoreEventStore>();
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<EfCoreEventStore>());

        services.AddHealthChecks()
            .AddDbContextCheck<FleanCommandDbContext>("database");
    }
}
