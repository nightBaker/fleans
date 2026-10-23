using System.Data.Common;
using Fleans.Persistence;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
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
    /// With <c>Fleans:Reminders:Provider=Postgres</c> it also creates the Orleans reminders
    /// schema — in the application database, or in <c>ConnectionStrings:fleans-reminders</c>
    /// when that dedicated database is configured (#886).
    /// </summary>
    public static async Task EnsureDatabaseSchemaAsync(this IHost app)
    {
        var options = app.Services.GetRequiredService<IOptions<FleansPersistenceOptions>>().Value;

        ResolvedRemindersConfiguration? postgresReminders = null;
        if (options.RemindersProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            postgresReminders = FleansRemindersExtensions.ResolveRemindersConfiguration(
                app.Services.GetRequiredService<IConfiguration>());

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

                if (postgresReminders is { DedicatedDatabase: false })
                    await EnsureOrleansRemindersSchemaAsync(db.Database.GetDbConnection());
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

            if (postgresReminders is { DedicatedDatabase: true } dedicated)
            {
                // Own database (#886): same advisory-lock dance on its own session.
                await using var conn = new NpgsqlConnection(dedicated.ConnectionString);
                await conn.OpenAsync();
                await ExecuteAsync(conn, $"SELECT pg_advisory_lock({MigrationLockKey})");
                try
                {
                    await EnsureOrleansRemindersSchemaAsync(conn);
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

    /// <summary>
    /// Runs the vendored Orleans Postgres reminders scripts once. Caller holds the advisory lock.
    /// The OrleansQuery table (created by Main.sql) is the sentinel: present → already
    /// initialised → skip; absent → run both.
    /// </summary>
    private static async Task EnsureOrleansRemindersSchemaAsync(DbConnection conn)
    {
        bool alreadyInitialised;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'orleansquery')";
            alreadyInitialised = (bool)(await cmd.ExecuteScalarAsync() ?? false);
        }

        if (alreadyInitialised)
            return;

        await ExecuteAsync(conn, await ReadEmbeddedScriptAsync("Fleans.ServiceDefaults.Resources.PostgreSQL_Main.sql"));
        await ExecuteAsync(conn, await ReadEmbeddedScriptAsync("Fleans.ServiceDefaults.Resources.PostgreSQL_Reminders.sql"));
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
