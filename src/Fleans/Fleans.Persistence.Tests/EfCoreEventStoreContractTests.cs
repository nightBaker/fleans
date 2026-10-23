using Fleans.Domain.Events;
using Fleans.Persistence.Events;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Orleans.Serialization;

namespace Fleans.Persistence.Tests;

/// <summary>
/// <see cref="EfCoreEventStore"/> through the <see cref="EventStoreContractTests"/> on every
/// journal provider, each on a database of its own holding only the journal — the separate-store
/// layout of <c>ConnectionStrings:fleans-journal</c> (#879).
/// </summary>
internal static class EfCoreEventStoreFactory
{
    private static readonly Serializer Serializer =
        new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<Serializer>();

    public static EfCoreEventStore Create(IDbContextFactory<FleanJournalDbContext> factory, int maxEventsPerLoad) =>
        new(factory, Serializer, Options.Create(new FleansPersistenceOptions { MaxEventsPerLoad = maxEventsPerLoad }));
}

[TestClass]
public class EfCoreSqliteEventStoreContractTests : EventStoreContractTests
{
    private SqliteConnection? _connection;

    protected override async Task<IEventStore> CreateStoreAsync(int maxEventsPerLoad = 1000)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        var factory = new TestJournalDbContextFactory(
            new DbContextOptionsBuilder<FleanJournalDbContext>().UseFleansSqlite(_connection).Options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(db.Database);
        }
        return EfCoreEventStoreFactory.Create(factory, maxEventsPerLoad);
    }

    protected override async ValueTask DisposeStoreAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

/// <summary>
/// A journal-only PostgreSQL database per class, migrated by the journal migrations alone — and on
/// Citus, distributed by <see cref="CitusJournal.DistributeAsync"/> as at startup. Tables are
/// truncated before each test.
/// </summary>
public abstract class PostgresJournalContractTestsBase : EventStoreContractTests
{
    private static readonly Dictionary<Type, (string Image, string Database, NpgsqlDataSource DataSource)> s_databases = new();

    protected static async Task CreateDatabaseAsync(Type testClass, string image, bool citus)
    {
        if (!PostgresContainerFixture.IsEnabled) return;

        var database = $"fleans_journal_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(PostgresContainerFixture.AdminConnectionString(image)))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var dataSource = NpgsqlDataSource.Create(PostgresContainerFixture.BuildConnectionString(image, database));
        if (citus)
        {
            await using var cmd = dataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS citus");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = new FleanJournalDbContext(PersistenceTestBase.BuildJournalOptions(dataSource)))
        {
            await db.Database.MigrateAsync();
            if (citus)
                await CitusJournal.DistributeAsync(db);
        }

        s_databases[testClass] = (image, database, dataSource);
    }

    protected static async Task DropDatabaseAsync(Type testClass)
    {
        if (!s_databases.Remove(testClass, out var entry)) return;

        await entry.DataSource.DisposeAsync();
        await using var admin = new NpgsqlConnection(PostgresContainerFixture.AdminConnectionString(entry.Image));
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{entry.Database}\" WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();
    }

    protected NpgsqlDataSource DataSource => s_databases[GetType()].DataSource;

    protected override async Task<IEventStore> CreateStoreAsync(int maxEventsPerLoad = 1000)
    {
        if (!PostgresContainerFixture.IsEnabled)
            Assert.Inconclusive("FLEANS_PG_TESTS != 1 — skipping the PostgreSQL journal contract run.");

        await using (var cmd = DataSource.CreateCommand("TRUNCATE TABLE \"WorkflowEventBatches\", \"WorkflowSnapshots\""))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        return EfCoreEventStoreFactory.Create(
            new TestJournalDbContextFactory(PersistenceTestBase.BuildJournalOptions(DataSource)), maxEventsPerLoad);
    }
}

[TestClass]
[TestCategory("Postgres")]
public class EfCorePostgresEventStoreContractTests : PostgresJournalContractTestsBase
{
    [ClassInitialize]
    public static Task InitAsync(TestContext _) =>
        CreateDatabaseAsync(typeof(EfCorePostgresEventStoreContractTests), PostgresContainerFixture.PostgresImage, citus: false);

    [ClassCleanup]
    public static Task CleanupAsync() => DropDatabaseAsync(typeof(EfCorePostgresEventStoreContractTests));
}

[TestClass]
[TestCategory("Postgres")]
public class EfCoreCitusEventStoreContractTests : PostgresJournalContractTestsBase
{
    [ClassInitialize]
    public static Task InitAsync(TestContext _) =>
        CreateDatabaseAsync(typeof(EfCoreCitusEventStoreContractTests), PostgresContainerFixture.CitusImage, citus: true);

    [ClassCleanup]
    public static Task CleanupAsync() => DropDatabaseAsync(typeof(EfCoreCitusEventStoreContractTests));

    [TestMethod]
    public async Task JournalTables_AreDistributedByInstanceId_AndColocated()
    {
        if (!PostgresContainerFixture.IsEnabled)
            Assert.Inconclusive("FLEANS_PG_TESTS != 1 — skipping the Citus journal run.");

        await using var cmd = DataSource.CreateCommand(
            """
            SELECT table_name::text, distribution_column, shard_count, colocation_id
            FROM citus_tables
            WHERE table_name IN ('"WorkflowEventBatches"'::regclass, '"WorkflowSnapshots"'::regclass)
            ORDER BY table_name::text
            """);
        var rows = new List<(string Table, string Column, int Shards, int Colocation)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1),
                    Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3))));
        }

        Assert.HasCount(2, rows);
        Assert.IsTrue(rows.All(r => r.Column == "InstanceId" && r.Shards == CitusJournal.ShardCount),
            string.Join("; ", rows));
        Assert.AreEqual(rows[0].Colocation, rows[1].Colocation, "snapshots must be colocated with their journal");

        // Applying the schema again (every silo does at startup) leaves the tables as they are.
        await using var db = new FleanJournalDbContext(PersistenceTestBase.BuildJournalOptions(DataSource));
        await CitusJournal.DistributeAsync(db);
    }
}
