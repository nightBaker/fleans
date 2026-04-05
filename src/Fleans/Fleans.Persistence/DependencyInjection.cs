using Fleans.Application;
using Fleans.Domain;
using Fleans.Domain.Events;
using Fleans.Domain.Persistence;
using Fleans.Persistence.Events;
using Microsoft.Data.Sqlite;
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
        var interceptor = new SqliteBusyTimeoutInterceptor();

        services.AddDbContextFactory<FleanCommandDbContext>(options =>
        {
            configureCommandDb(options);
            options.AddInterceptors(interceptor);
        });
        services.AddDbContextFactory<FleanQueryDbContext>(options =>
        {
            (configureQueryDb ?? configureCommandDb)(options);
            options.AddInterceptors(interceptor);
        });

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

    /// <summary>
    /// Creates the database schema if it doesn't exist, using file-based locking
    /// to prevent races when multiple processes (Api, Web, MCP) start concurrently.
    /// Also enables WAL mode for better concurrent read/write performance.
    /// </summary>
    public static void EnsureDatabaseCreated(IServiceProvider serviceProvider)
    {
        var dbFactory = serviceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
        using var db = dbFactory.CreateDbContext();

        var csb = new SqliteConnectionStringBuilder(db.Database.GetConnectionString());
        var lockPath = csb.DataSource + ".init-lock";

        // File lock prevents concurrent EnsureCreated from multiple processes.
        // Retry with backoff because Aspire starts all services concurrently and
        // FileStream with FileShare.None throws IOException immediately if locked.
        FileStream? fileLock = null;
        try
        {
            var maxAttempts = 20;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    fileLock = new FileStream(lockPath, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException) when (attempt < maxAttempts - 1)
                {
                    Thread.Sleep(500);
                }
            }

            db.Database.EnsureCreated();

            // WAL mode persists at the database level — set once, effective for all connections
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
        }
        finally
        {
            fileLock?.Dispose();

            try { File.Delete(lockPath); }
            catch (IOException) { /* another process may still hold the lock */ }
        }
    }
}
