using Fleans.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Base class for parametrised <see cref="PersistenceProvider"/> tests. Owns the
/// per-class PostgreSQL database lifecycle: <c>[ClassInitialize]</c> creates a fresh
/// <c>fleans_test_&lt;guid&gt;</c> database and runs <see cref="DatabaseFacade.MigrateAsync"/>;
/// <c>[TestInitialize]</c> truncates the model tables for per-test isolation;
/// <c>[ClassCleanup]</c> drops the database with <c>WITH (FORCE)</c>.
///
/// PG state is populated only when <c>FLEANS_PG_TESTS=1</c>; the SQLite path is unaffected.
///
/// MSTest execution is single-threaded by default. If
/// <c>[Parallelize(Scope = ExecutionScope.ClassLevel)]</c> is ever enabled, the static
/// fields below need to be promoted to a <c>ConcurrentDictionary&lt;Type,…&gt;</c> keyed
/// on the concrete derived class type to prevent races between sibling classes.
/// </summary>
public abstract class PersistenceTestBase
{
    protected internal static string PgDbName = string.Empty;
    protected internal static string PgConnectionString = string.Empty;
    protected internal static NpgsqlDataSource? PgCommandDataSource;
    protected internal static IReadOnlyList<string> CommandModelTables = Array.Empty<string>();
    protected internal static IReadOnlyList<string> QueryModelTables = Array.Empty<string>();

    protected static bool PgEnabled => PostgresContainerFixture.IsEnabled;

    [ClassInitialize(InheritanceBehavior.BeforeEachDerivedClass)]
    public static async Task InitClassAsync(TestContext _)
    {
        if (!PgEnabled) return;

        PgDbName = $"fleans_test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(PostgresContainerFixture.ContainerConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{PgDbName}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        PgConnectionString = PostgresContainerFixture.BuildConnectionString(PgDbName);
        PgCommandDataSource = NpgsqlDataSource.Create(PgConnectionString);

        await using (var commandCtx = new FleanCommandDbContext(BuildCommandOptions(PgCommandDataSource)))
        {
            await commandCtx.Database.MigrateAsync();
            CommandModelTables = commandCtx.Model.GetEntityTypes()
                .Select(t => t.GetTableName())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList()!;
        }

        await using (var queryCtx = new FleanQueryDbContext(BuildQueryOptions(PgCommandDataSource)))
        {
            QueryModelTables = queryCtx.Model.GetEntityTypes()
                .Select(t => t.GetTableName())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList()!;
        }
    }

    [ClassCleanup(InheritanceBehavior.BeforeEachDerivedClass)]
    public static async Task CleanupClassAsync()
    {
        if (!PgEnabled || string.IsNullOrEmpty(PgDbName)) return;

        if (PgCommandDataSource is { } ds)
        {
            await ds.DisposeAsync();
            PgCommandDataSource = null;
        }

        await using var admin = new NpgsqlConnection(PostgresContainerFixture.ContainerConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{PgDbName}\" WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();

        PgDbName = string.Empty;
        PgConnectionString = string.Empty;
        CommandModelTables = Array.Empty<string>();
        QueryModelTables = Array.Empty<string>();
    }

    /// <summary>
    /// Truncates every model-derived table in the per-class PG database to give each
    /// <c>[TestMethod]</c> a clean slate. SQLite rows are unaffected — they create a
    /// fresh in-memory database per fixture.
    /// </summary>
    [TestInitialize]
    public virtual async Task TruncateAsync()
    {
        if (!PgEnabled || PgCommandDataSource is null) return;

        var allTables = CommandModelTables
            .Concat(QueryModelTables)
            .Distinct()
            .Select(n => $"\"{n}\"");
        var tables = string.Join(", ", allTables);
        if (string.IsNullOrEmpty(tables)) return;

        await using var conn = PgCommandDataSource.CreateConnection();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"TRUNCATE TABLE {tables} RESTART IDENTITY CASCADE", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Builds command-context options against the supplied data source using the
    /// production <c>UseFleansPostgres</c> extension — guarantees the
    /// <c>PostgresModelCustomizer</c> is applied and migrations resolve from
    /// <c>Fleans.Persistence.PostgreSql</c>.
    /// </summary>
    protected internal static DbContextOptions<FleanCommandDbContext> BuildCommandOptions(NpgsqlDataSource ds) =>
        new DbContextOptionsBuilder<FleanCommandDbContext>().UseFleansPostgres(ds).Options;

    protected internal static DbContextOptions<FleanQueryDbContext> BuildQueryOptions(NpgsqlDataSource ds) =>
        new DbContextOptionsBuilder<FleanQueryDbContext>().UseFleansPostgres(ds).Options;
}
