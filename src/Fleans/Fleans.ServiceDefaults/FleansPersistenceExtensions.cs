using System.Data.Common;
using Fleans.Persistence;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.ServiceDefaults.Clustering;
using Fleans.ServiceDefaults.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

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

        builder.Services.Configure<FleansPersistenceOptions>(opts =>
        {
            opts.Provider = provider;
            builder.Configuration.GetSection("Persistence").Bind(opts);
            if (string.IsNullOrWhiteSpace(opts.Provider)) opts.Provider = provider;

            // Bind Fleans:Reminders:Provider here so EnsureDatabaseSchemaAsync can
            // see it via IOptions without re-reading IConfiguration after Build().
            var remindersProvider = builder.Configuration["Fleans:Reminders:Provider"];
            if (!string.IsNullOrWhiteSpace(remindersProvider)) opts.RemindersProvider = remindersProvider;
        });
        builder.Services.AddSingleton<IValidateOptions<FleansPersistenceOptions>, FleansPersistenceOptionsValidator>();
        builder.Services.AddOptions<FleansPersistenceOptions>().ValidateOnStart();

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
    ///
    /// With <c>Fleans:Reminders:Provider=Postgres</c> / <c>Fleans:Clustering:Provider=Postgres</c>
    /// it also creates the Orleans reminders / membership schema — in the application database,
    /// or in <c>ConnectionStrings:fleans-reminders</c> / <c>ConnectionStrings:fleans-cluster</c>
    /// when such a dedicated database is configured (#886, #885). It runs before the silo
    /// starts, so the membership table exists by the time the silo joins.
    /// </summary>
    public static async Task EnsureDatabaseSchemaAsync(this IHost app)
    {
        var options = app.Services.GetRequiredService<IOptions<FleansPersistenceOptions>>().Value;
        var configuration = app.Services.GetRequiredService<IConfiguration>();

        // Which Orleans scripts go where: the application database (null key) or a dedicated
        // database per connection string — reminders and membership may share one.
        var appDbSchema = OrleansSchema.None;
        var dedicatedSchemas = new Dictionary<string, OrleansSchema>(StringComparer.Ordinal);
        void Require(OrleansSchema part, string connectionString, bool dedicated)
        {
            if (!dedicated)
                appDbSchema |= part;
            else
                dedicatedSchemas[connectionString] = dedicatedSchemas.GetValueOrDefault(connectionString) | part;
        }

        if (options.RemindersProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var reminders = FleansRemindersExtensions.ResolveRemindersConfiguration(configuration);
            Require(OrleansSchema.Reminders, reminders.ConnectionString, reminders.DedicatedDatabase);
        }

        if (string.Equals(configuration[FleansClusteringOptions.ProviderKey], "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var clustering = FleansClusteringExtensions.ResolveClusteringConfiguration(configuration);
            Require(OrleansSchema.Clustering, clustering.ConnectionString, clustering.DedicatedDatabase);
        }

        using var scope = app.Services.CreateScope();
        var dbFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();

        if (options.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            await using var db = dbFactory.CreateDbContext();

            // Pin the connection so the session-level advisory lock survives across
            // MigrateAsync's internal transactions.
            await db.Database.OpenConnectionAsync();
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_lock({MigrationLockKey})");
                await db.Database.MigrateAsync();

                await EnsureOrleansSchemaAsync(db.Database.GetDbConnection(), appDbSchema);
            }
            finally
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"SELECT pg_advisory_unlock({MigrationLockKey})");
                }
                catch
                {
                    // Lock will be released on connection close anyway.
                }
                await db.Database.CloseConnectionAsync();
            }

            foreach (var (connectionString, schema) in dedicatedSchemas)
            {
                // Own database (#886, #885): same advisory-lock dance on its own session.
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                await ExecuteAsync(conn, $"SELECT pg_advisory_lock({MigrationLockKey})");
                try
                {
                    await EnsureOrleansSchemaAsync(conn, schema);
                }
                finally
                {
                    await ExecuteAsync(conn, $"SELECT pg_advisory_unlock({MigrationLockKey})");
                }
            }
        }
        else
        {
            using var db = dbFactory.CreateDbContext();
            SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(db.Database);
        }
    }

    // Arbitrary, but must be the same across all silos in the cluster.
    private const long MigrationLockKey = 8723547283L;

    [Flags]
    private enum OrleansSchema
    {
        None = 0,
        Reminders = 1,
        Clustering = 2,
    }

    /// <summary>
    /// Runs the vendored Orleans Postgres scripts that are still missing. Caller holds the
    /// advisory lock. Each script's main table is its sentinel: present → already initialised →
    /// skip. Main.sql (the OrleansQuery table both provider scripts insert into) runs first, once.
    /// </summary>
    private static async Task EnsureOrleansSchemaAsync(DbConnection conn, OrleansSchema schema)
    {
        if (schema == OrleansSchema.None)
            return;

        if (!await TableExistsAsync(conn, "orleansquery"))
            await ExecuteAsync(conn, await ReadEmbeddedScriptAsync("Fleans.ServiceDefaults.Resources.PostgreSQL_Main.sql"));

        if (schema.HasFlag(OrleansSchema.Reminders) && !await TableExistsAsync(conn, "orleansreminderstable"))
            await ExecuteAsync(conn, await ReadEmbeddedScriptAsync("Fleans.ServiceDefaults.Resources.PostgreSQL_Reminders.sql"));

        if (schema.HasFlag(OrleansSchema.Clustering) && !await TableExistsAsync(conn, "orleansmembershiptable"))
            await ExecuteAsync(conn, await ReadEmbeddedScriptAsync("Fleans.ServiceDefaults.Resources.PostgreSQL_Clustering.sql"));
    }

    /// <param name="relationName">Lower-case: the vendored scripts use unquoted identifiers.</param>
    private static async Task<bool> TableExistsAsync(DbConnection conn, string relationName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = '{relationName}')";
        return (bool)(await cmd.ExecuteScalarAsync() ?? false);
    }

    private static async Task ExecuteAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadEmbeddedScriptAsync(string resourceName)
    {
        var assembly = typeof(FleansPersistenceExtensions).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded SQL resource '{resourceName}' not found — check Fleans.ServiceDefaults.csproj <EmbeddedResource> entries.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
