using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Persistence.Sqlite;

/// <summary>
/// DI extensions for registering the SQLite-backed Fleans persistence layer.
/// </summary>
public static class SqlitePersistenceDependencyInjection
{
    /// <summary>
    /// Registers Fleans persistence using SQLite as the underlying provider for both
    /// command and query DbContexts.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="commandConnectionString">Connection string for the command DbContext.</param>
    /// <param name="queryConnectionString">
    /// Optional connection string for the query DbContext. When null the command connection
    /// string is reused (the existing behavior of <see cref="EfCorePersistenceDependencyInjection.AddEfCorePersistence"/>).
    /// </param>
    public static IServiceCollection AddSqlitePersistence(
        this IServiceCollection services,
        string commandConnectionString,
        string? queryConnectionString = null)
    {
        services.AddEfCorePersistence(
            options => options.UseFleansSqlite(commandConnectionString),
            queryConnectionString is not null
                ? options => options.UseFleansSqlite(queryConnectionString)
                : null);

        return services;
    }
}

/// <summary>
/// Extension helpers for configuring a <see cref="DbContextOptionsBuilder"/> to use
/// SQLite with the Fleans-specific model customizer applied.
/// </summary>
public static class SqliteDbContextOptionsExtensions
{
    /// <summary>
    /// Configures the builder to use SQLite and installs the
    /// <c>SqliteModelCustomizer</c> so SQLite-specific model tweaks (notably
    /// <see cref="DateTimeOffset"/> → string conversion) are applied.
    /// </summary>
    public static DbContextOptionsBuilder UseFleansSqlite(
        this DbContextOptionsBuilder builder,
        string connectionString)
    {
        builder.UseSqlite(connectionString);
        builder.ReplaceService<IModelCustomizer, SqliteModelCustomizer>();
        return builder;
    }

    /// <summary>
    /// Configures the builder to use SQLite against an existing
    /// <see cref="System.Data.Common.DbConnection"/> and installs the
    /// <c>SqliteModelCustomizer</c>.
    /// </summary>
    public static DbContextOptionsBuilder UseFleansSqlite(
        this DbContextOptionsBuilder builder,
        System.Data.Common.DbConnection connection)
    {
        builder.UseSqlite(connection);
        builder.ReplaceService<IModelCustomizer, SqliteModelCustomizer>();
        return builder;
    }

    /// <inheritdoc cref="UseFleansSqlite(DbContextOptionsBuilder, string)"/>
    public static DbContextOptionsBuilder<TContext> UseFleansSqlite<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext
    {
        UseFleansSqlite((DbContextOptionsBuilder)builder, connectionString);
        return builder;
    }

    /// <inheritdoc cref="UseFleansSqlite(DbContextOptionsBuilder, System.Data.Common.DbConnection)"/>
    public static DbContextOptionsBuilder<TContext> UseFleansSqlite<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        System.Data.Common.DbConnection connection)
        where TContext : DbContext
    {
        UseFleansSqlite((DbContextOptionsBuilder)builder, connection);
        return builder;
    }
}
