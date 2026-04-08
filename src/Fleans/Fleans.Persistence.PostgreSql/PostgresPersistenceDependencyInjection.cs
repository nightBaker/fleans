using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Fleans.Persistence.PostgreSql;

/// <summary>
/// DI extensions for registering the PostgreSQL-backed Fleans persistence layer.
/// </summary>
public static class PostgresPersistenceDependencyInjection
{
    /// <summary>
    /// Registers Fleans persistence using PostgreSQL as the underlying provider for both
    /// command and query DbContexts.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="connectionString">
    /// PostgreSQL connection string. An <see cref="NpgsqlDataSource"/> is built from this
    /// string and registered as a singleton so it is disposed cleanly on host shutdown.
    /// </param>
    /// <param name="queryConnectionString">
    /// Optional connection string for a read-only replica. When null the primary
    /// connection string is reused for both command and query contexts.
    /// </param>
    public static IServiceCollection AddPostgresPersistence(
        this IServiceCollection services,
        string connectionString,
        string? queryConnectionString = null)
    {
        // Build NpgsqlDataSource singletons (owned by DI — disposed on host shutdown).
        // Using NpgsqlDataSource is the recommended pattern for Npgsql 8+.
        var commandDataSource = NpgsqlDataSource.Create(connectionString);
        services.AddSingleton(commandDataSource);

        NpgsqlDataSource? queryDataSource = null;
        if (queryConnectionString is not null && queryConnectionString != connectionString)
        {
            queryDataSource = NpgsqlDataSource.Create(queryConnectionString);
            // Register under a distinct key so DI owns its lifetime (disposed on host shutdown)
            // without shadowing the command data source resolved via GetService<NpgsqlDataSource>().
            // Future read-replica wiring should resolve via GetKeyedService<NpgsqlDataSource>("fleans-query").
            services.AddKeyedSingleton("fleans-query", queryDataSource);
        }

        var effectiveQueryDataSource = queryDataSource ?? commandDataSource;

        services.AddEfCorePersistence(
            options => options.UseFleansPostgres(commandDataSource),
            queryConnectionString is not null
                ? options => options.UseFleansPostgres(effectiveQueryDataSource)
                : null);

        return services;
    }
}

/// <summary>
/// Extension helpers for configuring a <see cref="DbContextOptionsBuilder"/> to use
/// PostgreSQL with the Fleans-specific model customizer applied.
/// </summary>
public static class PostgresDbContextOptionsExtensions
{
    /// <summary>
    /// Configures the builder to use PostgreSQL via an <see cref="NpgsqlDataSource"/> and
    /// installs the <c>PostgresModelCustomizer</c>.
    /// Migrations assembly is always set to <c>Fleans.Persistence.PostgreSql</c>.
    /// </summary>
    public static DbContextOptionsBuilder UseFleansPostgres(
        this DbContextOptionsBuilder builder,
        NpgsqlDataSource dataSource,
        Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? npgsqlOptions = null)
    {
        builder.UseNpgsql(dataSource, npg =>
        {
            npg.MigrationsAssembly("Fleans.Persistence.PostgreSql");
            npgsqlOptions?.Invoke(npg);
        });
        builder.ReplaceService<IModelCustomizer, PostgresModelCustomizer>();
        return builder;
    }

    /// <inheritdoc cref="UseFleansPostgres(DbContextOptionsBuilder, NpgsqlDataSource, Action{Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder}?)"/>
    public static DbContextOptionsBuilder<TContext> UseFleansPostgres<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        NpgsqlDataSource dataSource,
        Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? npgsqlOptions = null)
        where TContext : DbContext
    {
        UseFleansPostgres((DbContextOptionsBuilder)builder, dataSource, npgsqlOptions);
        return builder;
    }
}
