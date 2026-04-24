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

        services.AddSingleton<IProcessDefinitionRepository, EfCoreProcessDefinitionRepository>();
        services.AddSingleton<ISieveProcessor, ApplicationSieveProcessor>();
        services.Configure<SieveOptions>(options =>
        {
            options.DefaultPageSize = 20;
            options.MaxPageSize = 100;
        });
        services.AddSingleton<IWorkflowQueryService, WorkflowQueryService>();
        services.AddSingleton<IWorkflowStateProjection, EfCoreWorkflowStateProjection>();
        services.AddSingleton<EfCoreEventStore>();
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<EfCoreEventStore>());

        services.AddHealthChecks()
            .AddDbContextCheck<FleanCommandDbContext>("database");
    }
}
