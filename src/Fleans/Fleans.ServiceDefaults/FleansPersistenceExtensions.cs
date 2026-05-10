using Fleans.Persistence;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Fleans.ServiceDefaults;

public static class FleansPersistenceExtensions
{
    /// <summary>
    /// Configures Fleans EF Core persistence based on the Persistence:Provider config key.
    /// Supports "Sqlite" (default) and "Postgres".
    /// Registers <see cref="FleansPersistenceOptions"/> so that
    /// <see cref="EnsureDatabaseSchemaAsync"/> can resolve the provider choice
    /// without re-reading configuration.
    /// </summary>
    public static IHostApplicationBuilder AddFleansPersistence(this IHostApplicationBuilder builder)
    {
        var provider = builder.Configuration["Persistence:Provider"] ?? "Sqlite";

        builder.Services.Configure<FleansPersistenceOptions>(opts => opts.Provider = provider);

        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var connStr = builder.Configuration.GetConnectionString("fleans")
                ?? throw new InvalidOperationException(
                    "Connection string 'fleans' is required when Persistence:Provider=Postgres");
            var queryConnStr = builder.Configuration.GetConnectionString("fleans-query");
            builder.Services.AddPostgresPersistence(connStr, queryConnStr);
        }
        else if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connStr = builder.Configuration["FLEANS_SQLITE_CONNECTION"] ?? "DataSource=fleans-dev.db";
            var queryConnStr = builder.Configuration["FLEANS_QUERY_CONNECTION"];
            builder.Services.AddSqlitePersistence(connStr, queryConnStr);
        }
        else
        {
            throw new ArgumentException(
                $"Unknown persistence provider '{provider}'. Supported: Sqlite, Postgres. " +
                $"Set Persistence:Provider in configuration.");
        }

        return builder;
    }

    /// <summary>
    /// Ensures the database schema is ready. Call after builder.Build().
    /// For SQLite: EnsureCreated (idempotent, race-safe).
    /// For PostgreSQL: MigrateAsync wrapped in a Postgres advisory lock so multiple silos
    /// (Api / Web / Mcp / Worker) all calling this concurrently serialize cleanly.
    ///
    /// Without the lock, concurrent MigrateAsync calls race — EF Core's per-migration lock
    /// is acquired only while writing __EFMigrationsHistory, not while running migration
    /// SQL. Two silos that both observe a migration as pending can therefore both try to
    /// CREATE TABLE, and the loser fails with `relation "X" already exists`.
    /// </summary>
    public static async Task EnsureDatabaseSchemaAsync(this IHost app)
    {
        var options = app.Services.GetRequiredService<IOptions<FleansPersistenceOptions>>().Value;

        using var scope = app.Services.CreateScope();
        var dbFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();

        if (options.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            await using var db = dbFactory.CreateDbContext();

            // Pin the connection so the session-level advisory lock survives across
            // MigrateAsync's internal transactions. The key is arbitrary but must be the
            // same across all silos in the cluster.
            const long migrationLockKey = 8723547283L;
            await db.Database.OpenConnectionAsync();
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_lock({migrationLockKey})");
                await db.Database.MigrateAsync();
            }
            finally
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"SELECT pg_advisory_unlock({migrationLockKey})");
                }
                catch
                {
                    // Lock will be released on connection close anyway.
                }
                await db.Database.CloseConnectionAsync();
            }
        }
        else
        {
            using var db = dbFactory.CreateDbContext();
            SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(db.Database);
        }
    }
}
