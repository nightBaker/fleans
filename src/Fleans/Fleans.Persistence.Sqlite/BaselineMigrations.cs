using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Persistence.Sqlite;

/// <summary>
/// Helpers for adopting EF Core migrations on existing SQLite databases that were
/// originally created via <see cref="DatabaseFacade.EnsureCreated"/>.
/// </summary>
/// <remarks>
/// <para>
/// When a database was created with <c>EnsureCreated()</c> it has no
/// <c>__EFMigrationsHistory</c> table, so calling <c>Migrate()</c> would attempt to
/// re-create all tables and fail. <see cref="ApplyBaselineAsync"/> detects this
/// condition and idempotently records the <c>Initial</c> migration as already applied,
/// allowing subsequent calls to <c>MigrateAsync()</c> to succeed.
/// </para>
/// <para>
/// This helper is intentionally idempotent: running it multiple times is safe.
/// </para>
/// </remarks>
public static class BaselineMigrations
{
    /// <summary>
    /// Migration ID for <see cref="FleanCommandDbContext"/>'s Initial migration.
    /// </summary>
    public const string CommandInitialMigrationId = "20260408122820_Initial";

    /// <summary>
    /// Migration ID for <see cref="FleanQueryDbContext"/>'s Initial migration.
    /// </summary>
    public const string QueryInitialMigrationId = "20260408122831_Initial";

    /// <summary>
    /// Idempotently records the <c>Initial</c> migration as applied for a database that was
    /// previously created with <c>EnsureCreated()</c>.
    ///
    /// Conditions checked:
    /// <list type="bullet">
    ///   <item>At least one application table exists (the schema was already applied).</item>
    ///   <item>The <c>__EFMigrationsHistory</c> row for <paramref name="migrationId"/> is absent.</item>
    /// </list>
    /// If both conditions are met, a single baseline row is inserted. Otherwise nothing happens.
    /// </summary>
    /// <param name="context">The <see cref="DbContext"/> whose database to baseline.</param>
    /// <param name="migrationId">
    /// The migration ID to record (e.g. <see cref="CommandInitialMigrationId"/>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task ApplyBaselineAsync(
        DbContext context,
        string migrationId,
        CancellationToken cancellationToken = default)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = connection.CreateCommand();

            // Check whether a known application table exists (proves EnsureCreated ran)
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProcessDefinitions'";
            var tableExists = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!tableExists)
                return; // Fresh database — let Migrate() handle it normally

            // Ensure the migrations history table exists
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                )
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            // Insert the baseline row if it hasn't been recorded yet (parameterized to prevent SQL injection)
            var productVersion = ProductInfo.GetVersion();
            cmd.CommandText = """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                SELECT @migrationId, @productVersion
                WHERE NOT EXISTS (
                    SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId
                )
                """;
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@migrationId"; p1.Value = migrationId;
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@productVersion"; p2.Value = productVersion;
            cmd.Parameters.Add(p1);
            cmd.Parameters.Add(p2);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}

/// <summary>
/// Extension methods for migrating Fleans SQLite databases from an
/// <c>EnsureCreated()</c> baseline to EF Core migrations.
/// </summary>
public static class SqliteMigrationExtensions
{
    /// <summary>
    /// Migrates both <see cref="FleanCommandDbContext"/> and <see cref="FleanQueryDbContext"/>
    /// to the latest schema using EF Core migrations.
    ///
    /// <para>
    /// For databases previously created with <c>EnsureCreated()</c>, this method first
    /// applies a baseline so EF Core's migration runner does not attempt to recreate
    /// existing tables. The baseline step is idempotent and safe to re-run.
    /// </para>
    ///
    /// <para>
    /// Call this method once on startup (e.g. from <c>Program.cs</c>) to opt in to
    /// migration-based schema management. The default <c>EnsureCreated()</c> path
    /// (via <see cref="SqliteSchemaInitializer"/>) is unaffected.
    /// </para>
    /// </summary>
    public static async Task MigrateWithBaselineAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var commandFactory = sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
        await using var commandDb = commandFactory.CreateDbContext();
        await BaselineMigrations.ApplyBaselineAsync(
            commandDb, BaselineMigrations.CommandInitialMigrationId, cancellationToken);
        await commandDb.Database.MigrateAsync(cancellationToken);

        var queryFactory = sp.GetRequiredService<IDbContextFactory<FleanQueryDbContext>>();
        await using var queryDb = queryFactory.CreateDbContext();
        await BaselineMigrations.ApplyBaselineAsync(
            queryDb, BaselineMigrations.QueryInitialMigrationId, cancellationToken);
        await queryDb.Database.MigrateAsync(cancellationToken);
    }
}
