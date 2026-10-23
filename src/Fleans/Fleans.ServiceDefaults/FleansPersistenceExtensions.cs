using Fleans.Persistence;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
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
        var journalProvider = builder.Configuration["Persistence:Journal:Provider"];
        if (string.IsNullOrWhiteSpace(journalProvider)) journalProvider = provider;

        builder.Services.Configure<FleansPersistenceOptions>(opts =>
        {
            opts.Provider = provider;
            builder.Configuration.GetSection("Persistence").Bind(opts);
            if (string.IsNullOrWhiteSpace(opts.Provider)) opts.Provider = provider;
            opts.Journal.Provider = journalProvider;

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
            builder.Services.AddPostgresPersistence(connStr, queryConnStr,
                ConfigureJournal(builder, provider, connStr, journalProvider));
        }
        else if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connStr = builder.Configuration["FLEANS_SQLITE_CONNECTION"] ?? "DataSource=fleans-dev.db";
            var queryConnStr = builder.Configuration["FLEANS_QUERY_CONNECTION"];
            builder.Services.AddSqlitePersistence(connStr, queryConnStr,
                ConfigureJournal(builder, provider, connStr, journalProvider));
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
    /// The journal store's options (#879), or <c>null</c> when the journal shares the main
    /// database: no <c>ConnectionStrings:fleans-journal</c> and a journal provider of the main
    /// provider's family (Citus is PostgreSQL). Any other combination needs
    /// <c>ConnectionStrings:fleans-journal</c>.
    /// </summary>
    private static Action<DbContextOptionsBuilder>? ConfigureJournal(
        IHostApplicationBuilder builder, string provider, string mainConnectionString, string journalProvider)
    {
        var isPostgres = IsPostgresFamily(journalProvider);
        if (!isPostgres && !journalProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Unknown journal provider '{journalProvider}'. Supported: Sqlite, Postgres, Citus. " +
                "Set Persistence:Journal:Provider in configuration, or leave it empty for Persistence:Provider.");

        var journalConnStr = builder.Configuration.GetConnectionString("fleans-journal");
        if (string.IsNullOrWhiteSpace(journalConnStr) || journalConnStr == mainConnectionString)
        {
            if (isPostgres != IsPostgresFamily(provider))
                throw new InvalidOperationException(
                    $"Connection string 'fleans-journal' is required when Persistence:Journal:Provider={journalProvider} " +
                    $"and Persistence:Provider={provider}.");
            return null; // the main database, through its own data source
        }

        if (!isPostgres)
            return options => options.UseFleansSqlite(journalConnStr);

        var journalDataSource = NpgsqlDataSource.Create(journalConnStr);
        // Owned by DI so it is disposed on host shutdown; keyed so it never shadows the main one.
        builder.Services.AddKeyedSingleton("fleans-journal", journalDataSource);
        return options => options.UseFleansPostgresJournal(journalDataSource);
    }

    private static bool IsPostgresFamily(string provider) =>
        provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("Citus", StringComparison.OrdinalIgnoreCase);

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
    /// The journal context (#879) follows, in its own database or the main one: its own
    /// migrations (own history table) under the same lock, then — for Citus — the
    /// distribution of its tables. Main first: on a shared database the main migrations drop
    /// the journal tables they created before the journal moved to its own context.
    /// </summary>
    public static async Task EnsureDatabaseSchemaAsync(this IHost app)
    {
        var options = app.Services.GetRequiredService<IOptions<FleansPersistenceOptions>>().Value;

        using var scope = app.Services.CreateScope();
        var dbFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await EnsureSchemaAsync(db, options.Provider,
                options.RemindersProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
                    ? EnsureOrleansRemindersSchemaAsync
                    : null);
        }

        var journalProvider = options.Journal.Provider ?? options.Provider;
        var journalFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<FleanJournalDbContext>>();
        await using (var journal = await journalFactory.CreateDbContextAsync())
        {
            await EnsureSchemaAsync(journal, journalProvider,
                journalProvider.Equals("Citus", StringComparison.OrdinalIgnoreCase)
                    ? db => CitusJournal.DistributeAsync((FleanJournalDbContext)db)
                    : null);
        }
    }

    /// <summary>
    /// SQLite: EnsureCreated. PostgreSQL / Citus: MigrateAsync, then
    /// <paramref name="afterMigrate"/>, under the migration advisory lock.
    /// </summary>
    private static async Task EnsureSchemaAsync(
        Microsoft.EntityFrameworkCore.DbContext db, string provider, Func<Microsoft.EntityFrameworkCore.DbContext, Task>? afterMigrate)
    {
        if (IsPostgresFamily(provider))
        {
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
                if (afterMigrate is not null)
                    await afterMigrate(db);
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
            SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(db.Database);
        }
    }

    private static async Task EnsureOrleansRemindersSchemaAsync(Microsoft.EntityFrameworkCore.DbContext db)
    {
        // Idempotent guard. ExecuteSqlRawAsync returns -1 for SELECT in Npgsql
        // (rows-affected is undefined for queries) so we use raw ADO.NET
        // ExecuteScalarAsync. The OrleansQuery table (created by Main.sql) is
        // the sentinel: present → already initialised → skip; absent → run both.
        bool alreadyInitialised;
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'orleansquery')";
            alreadyInitialised = (bool)(await cmd.ExecuteScalarAsync() ?? false);
        }

        if (!alreadyInitialised)
        {
            await ExecuteEmbeddedScriptAsync(db, "Fleans.ServiceDefaults.Resources.PostgreSQL_Main.sql");
            await ExecuteEmbeddedScriptAsync(db, "Fleans.ServiceDefaults.Resources.PostgreSQL_Reminders.sql");
        }
    }

    private static async Task ExecuteEmbeddedScriptAsync(Microsoft.EntityFrameworkCore.DbContext db, string resourceName)
    {
        var assembly = typeof(FleansPersistenceExtensions).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded SQL resource '{resourceName}' not found — check Fleans.ServiceDefaults.csproj <EmbeddedResource> entries.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();
        await db.Database.ExecuteSqlRawAsync(sql);
    }
}
