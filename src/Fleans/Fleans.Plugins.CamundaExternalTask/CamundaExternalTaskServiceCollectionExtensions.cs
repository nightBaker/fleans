using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Fleans.Plugins.CamundaExternalTask;

public static class CamundaExternalTaskServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Camunda external-task plugin on a silo host: the stream handler grain (found
    /// by Orleans assembly scanning), the external-task table and the service behind
    /// <see cref="CamundaExternalTaskEndpoints.MapCamundaExternalTaskApi"/>.
    /// </summary>
    /// <param name="configureDb">Provider for the external-task table, e.g. <c>o =&gt; o.UseNpgsql(cs)</c>.</param>
    public static IServiceCollection AddCamundaExternalTasks(
        this IServiceCollection services, Action<DbContextOptionsBuilder> configureDb)
    {
        ArgumentNullException.ThrowIfNull(configureDb);
        services.AddDbContextFactory<ExternalTaskDbContext>(configureDb);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ExternalTaskService>();
        services.AddHostedService<ExternalTaskSchemaInitializer>();
        return services;
    }
}

/// <summary>
/// Creates the <c>camunda_external_tasks</c> table and its indexes at startup if they do not exist
/// yet. Works on a dedicated database and on one shared with other tables (where
/// <c>EnsureCreated</c> would be a no-op), and is safe when several replicas start at once.
/// Relies on <c>CREATE TABLE/INDEX IF NOT EXISTS</c> (SQLite, PostgreSQL).
/// </summary>
internal sealed class ExternalTaskSchemaInitializer(IDbContextFactory<ExternalTaskDbContext> dbFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Two replicas creating the same object at once can still collide inside the database
        // (e.g. PostgreSQL's pg_type unique index), even with IF NOT EXISTS — the loser retries,
        // and by then the object exists.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CreateSchema(cancellationToken);
                return;
            }
            catch (Exception) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private const int MaxAttempts = 3;

    private async Task CreateSchema(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken))
            await creator.CreateAsync(cancellationToken);

        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
        await db.Database.ExecuteSqlRawAsync(script, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
