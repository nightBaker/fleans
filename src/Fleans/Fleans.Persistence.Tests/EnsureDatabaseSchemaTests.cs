using DotNet.Testcontainers.Builders;
using Fleans.Persistence.Tests.Infrastructure;
using Fleans.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Fleans.Persistence.Tests;

/// <summary>
/// <see cref="FleansPersistenceExtensions.EnsureDatabaseSchemaAsync"/> against real databases,
/// with the journal in the main database (the default) and in a Citus database of its own (#879).
/// </summary>
[TestClass]
[TestCategory("Postgres")]
public class EnsureDatabaseSchemaTests
{
    [TestMethod]
    public async Task Default_JournalTablesLiveInTheMainDatabase()
    {
        Skip.IfPostgresUnavailable(PersistenceProvider.Postgres);
        var main = await CreateDatabaseAsync(PostgresContainerFixture.PostgresImage);
        try
        {
            var config = new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Postgres",
                ["ConnectionStrings:fleans"] = main,
            };

            await EnsureSchemaAsync(config);
            await EnsureSchemaAsync(config); // every silo applies it at startup

            CollectionAssert.IsSubsetOf(
                new[] { "WorkflowInstances", "WorkflowEventBatches", "WorkflowSnapshots" },
                await TablesAsync(main));
        }
        finally
        {
            await DropDatabaseAsync(PostgresContainerFixture.PostgresImage, main);
        }
    }

    [TestMethod]
    public async Task CitusJournal_GetsOnlyTheJournalTables_Distributed()
    {
        Skip.IfPostgresUnavailable(PersistenceProvider.Postgres);
        var main = await CreateDatabaseAsync(PostgresContainerFixture.PostgresImage);
        var journal = await CreateDatabaseAsync(PostgresContainerFixture.CitusImage);
        try
        {
            await using (var cmd = new NpgsqlConnection(journal))
            {
                await cmd.OpenAsync();
                await new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS citus", cmd).ExecuteNonQueryAsync();
            }
            var config = new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Postgres",
                ["Persistence:Journal:Provider"] = "Citus",
                ["Persistence:Journal:Citus:ShardCount"] = "16",
                ["ConnectionStrings:fleans"] = main,
                ["ConnectionStrings:fleans-journal"] = journal,
            };

            await EnsureSchemaAsync(config);
            await EnsureSchemaAsync(config);

            var mainTables = await TablesAsync(main);
            var journalTables = await TablesAsync(journal);
            CollectionAssert.Contains(mainTables, "WorkflowInstances");
            CollectionAssert.DoesNotContain(mainTables, "WorkflowEventBatches");
            CollectionAssert.DoesNotContain(mainTables, "WorkflowSnapshots");
            CollectionAssert.IsSubsetOf(new[] { "WorkflowEventBatches", "WorkflowSnapshots" }, journalTables);
            CollectionAssert.DoesNotContain(journalTables, "WorkflowInstances");

            await using var conn = new NpgsqlConnection(journal);
            await conn.OpenAsync();
            var distributed = Convert.ToInt32(await new NpgsqlCommand(
                "SELECT count(*) FROM citus_tables WHERE distribution_column = 'InstanceId' AND shard_count = 16", conn).ExecuteScalarAsync());
            Assert.AreEqual(2, distributed);
        }
        finally
        {
            await DropDatabaseAsync(PostgresContainerFixture.PostgresImage, main);
            await DropDatabaseAsync(PostgresContainerFixture.CitusImage, journal);
        }
    }

    [TestMethod]
    public async Task CitusJournal_WithoutTheExtension_FailsWithAClearMessage()
    {
        Skip.IfPostgresUnavailable(PersistenceProvider.Postgres);
        var main = await CreateDatabaseAsync(PostgresContainerFixture.PostgresImage);
        try
        {
            var config = new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Postgres",
                ["Persistence:Journal:Provider"] = "Citus",
                ["ConnectionStrings:fleans"] = main,
            };

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => EnsureSchemaAsync(config));
            StringAssert.Contains(ex.Message, "CREATE EXTENSION citus");
        }
        finally
        {
            await DropDatabaseAsync(PostgresContainerFixture.PostgresImage, main);
        }
    }

    [TestMethod]
    public async Task ThroughPgBouncerInTransactionMode_ConcurrentSilosApplyTheSchemaAndLeaveNoLockBehind()
    {
        // #923: with a session-level advisory lock, the lock, the migrations and the unlock each go
        // to whatever server connection the pooler hands out; the unlock misses and the lock stays
        // held by a pooled server connection, so the next silo waits on it forever.
        Skip.IfPostgresUnavailable(PersistenceProvider.Postgres);
        var direct = await CreateDatabaseAsync(PostgresContainerFixture.PostgresImage);
        var target = new NpgsqlConnectionStringBuilder(direct);
        await using var bouncer = new ContainerBuilder()
            .WithImage("edoburu/pgbouncer:v1.24.1-p1")
            .WithEnvironment("DB_HOST", PostgresContainerFixture.BridgeAddress(PostgresContainerFixture.PostgresImage))
            .WithEnvironment("DB_PORT", "5432")
            .WithEnvironment("DB_USER", target.Username)
            .WithEnvironment("DB_PASSWORD", target.Password)
            .WithEnvironment("DB_NAME", target.Database)
            .WithEnvironment("AUTH_TYPE", "scram-sha-256")
            .WithEnvironment("POOL_MODE", "transaction")
            .WithEnvironment("DEFAULT_POOL_SIZE", "4")
            .WithPortBinding(5432, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5432))
            .Build();
        await bouncer.StartAsync();
        var viaBouncer = new NpgsqlConnectionStringBuilder(direct)
        {
            Host = bouncer.Hostname,
            Port = bouncer.GetMappedPublicPort(5432),
            Pooling = true,
        }.ToString();
        try
        {
            var config = new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Postgres",
                ["ConnectionStrings:fleans"] = viaBouncer,
            };

            var silos = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => EnsureSchemaAsync(config)));
            var finished = await Task.WhenAny(silos, Task.Delay(TimeSpan.FromSeconds(90)));

            Assert.AreSame(silos, finished, "Schema setup through PgBouncer did not finish: a silo waits on a leaked lock.");
            await silos;
            CollectionAssert.IsSubsetOf(new[] { "WorkflowInstances", "WorkflowEventBatches" }, await TablesAsync(direct));
            Assert.AreEqual(0L, await AdvisoryLocksAsync(direct), "The migration lock outlived the schema setup.");
        }
        finally
        {
            await DropDatabaseAsync(PostgresContainerFixture.PostgresImage, direct);
        }
    }

    private static async Task<long> AdvisoryLocksAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory'", conn).ExecuteScalarAsync())!;
    }

    private static async Task EnsureSchemaAsync(IDictionary<string, string?> config)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(config);
        builder.AddFleansPersistence();
        using var host = builder.Build();
        await host.EnsureDatabaseSchemaAsync();
    }

    private static async Task<string> CreateDatabaseAsync(string image)
    {
        var database = $"fleans_schema_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(PostgresContainerFixture.AdminConnectionString(image));
        await admin.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin).ExecuteNonQueryAsync();
        // No pooling: the database is dropped at the end of the test.
        return new NpgsqlConnectionStringBuilder(PostgresContainerFixture.BuildConnectionString(image, database))
        {
            Pooling = false,
        }.ToString();
    }

    private static async Task DropDatabaseAsync(string image, string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await using var admin = new NpgsqlConnection(PostgresContainerFixture.AdminConnectionString(image));
        await admin.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> TablesAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var reader = await new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'", conn).ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        return tables;
    }
}
