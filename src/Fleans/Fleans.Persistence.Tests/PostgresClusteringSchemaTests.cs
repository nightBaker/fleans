using Fleans.Persistence.Tests.Infrastructure;
using Fleans.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Fleans.Persistence.Tests;

/// <summary>
/// #885: <c>Fleans:Clustering:Provider=Postgres</c> — EnsureDatabaseSchemaAsync creates the Orleans
/// membership schema, in the application database or in <c>ConnectionStrings:fleans-cluster</c>, next to
/// (or together with) the Postgres reminders schema. Runs only with <c>FLEANS_PG_TESTS=1</c> (Docker).
/// </summary>
[TestClass]
public class PostgresClusteringSchemaTests
{
    [TestMethod]
    public async Task Shared_database_gets_membership_and_reminders_schema_idempotently()
    {
        if (!PostgresContainerFixture.IsEnabled)
            Assert.Inconclusive("FLEANS_PG_TESTS != 1 — skipping Postgres clustering schema test.");

        var appDb = $"fleans_cluster_shared_{Guid.NewGuid().ToString("N")[..8]}";
        await ExecuteAdminAsync($"CREATE DATABASE \"{appDb}\"");
        try
        {
            using var host = BuildHost(new()
            {
                ["Fleans:Clustering:Provider"] = "Postgres",
                ["Fleans:Reminders:Provider"] = "Postgres",
                ["ConnectionStrings:fleans"] = PostgresContainerFixture.BuildConnectionString(appDb),
            });

            await host.EnsureDatabaseSchemaAsync();
            await host.EnsureDatabaseSchemaAsync(); // a second silo start is a no-op

            Assert.IsTrue(await TableExistsAsync(appDb, "orleansmembershiptable"));
            Assert.IsTrue(await TableExistsAsync(appDb, "orleansreminderstable"));
            Assert.IsTrue(await MembershipQueriesRegisteredAsync(appDb),
                "PostgreSQL-Clustering.sql must register the membership queries in OrleansQuery.");
        }
        finally
        {
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{appDb}\" WITH (FORCE)");
        }
    }

    [TestMethod]
    public async Task Membership_is_added_to_a_database_that_already_has_the_reminders_schema()
    {
        if (!PostgresContainerFixture.IsEnabled)
            Assert.Inconclusive("FLEANS_PG_TESTS != 1 — skipping Postgres clustering schema test.");

        var appDb = $"fleans_cluster_later_{Guid.NewGuid().ToString("N")[..8]}";
        await ExecuteAdminAsync($"CREATE DATABASE \"{appDb}\"");
        try
        {
            var connectionString = PostgresContainerFixture.BuildConnectionString(appDb);
            using (var remindersOnly = BuildHost(new()
                   {
                       ["Fleans:Reminders:Provider"] = "Postgres",
                       ["ConnectionStrings:fleans"] = connectionString,
                   }))
                await remindersOnly.EnsureDatabaseSchemaAsync();

            Assert.IsFalse(await TableExistsAsync(appDb, "orleansmembershiptable"));

            // OrleansQuery already exists (reminders created it); membership must still be created.
            using var withClustering = BuildHost(new()
            {
                ["Fleans:Clustering:Provider"] = "Postgres",
                ["Fleans:Reminders:Provider"] = "Postgres",
                ["ConnectionStrings:fleans"] = connectionString,
            });
            await withClustering.EnsureDatabaseSchemaAsync();

            Assert.IsTrue(await TableExistsAsync(appDb, "orleansmembershiptable"));
        }
        finally
        {
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{appDb}\" WITH (FORCE)");
        }
    }

    [TestMethod]
    public async Task Dedicated_cluster_database_gets_the_membership_schema()
    {
        if (!PostgresContainerFixture.IsEnabled)
            Assert.Inconclusive("FLEANS_PG_TESTS != 1 — skipping Postgres clustering schema test.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appDb = $"fleans_app_{suffix}";
        var clusterDb = $"fleans_cluster_{suffix}";
        await ExecuteAdminAsync($"CREATE DATABASE \"{appDb}\"");
        await ExecuteAdminAsync($"CREATE DATABASE \"{clusterDb}\"");
        try
        {
            using var host = BuildHost(new()
            {
                ["Fleans:Clustering:Provider"] = "Postgres",
                ["ConnectionStrings:fleans"] = PostgresContainerFixture.BuildConnectionString(appDb),
                ["ConnectionStrings:fleans-cluster"] = PostgresContainerFixture.BuildConnectionString(clusterDb),
            });

            await host.EnsureDatabaseSchemaAsync();
            await host.EnsureDatabaseSchemaAsync();

            Assert.IsTrue(await TableExistsAsync(clusterDb, "orleansmembershiptable"),
                "Membership table must be created in the dedicated cluster database.");
            Assert.IsFalse(await TableExistsAsync(clusterDb, "orleansreminderstable"),
                "Reminders stay on Redis here — their schema must not be created.");
            Assert.IsFalse(await TableExistsAsync(appDb, "orleansmembershiptable"),
                "Membership table must not be created in the application database.");
            Assert.IsTrue(await TableExistsAsync(appDb, "WorkflowInstances"));
        }
        finally
        {
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{appDb}\" WITH (FORCE)");
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{clusterDb}\" WITH (FORCE)");
        }
    }

    private static IHost BuildHost(Dictionary<string, string?> values)
    {
        values["Persistence:Provider"] = "Postgres";
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(values);
        builder.AddFleansPersistence();
        return builder.Build();
    }

    private static async Task ExecuteAdminAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(PostgresContainerFixture.ContainerConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(string database, string table)
    {
        await using var conn = new NpgsqlConnection(PostgresContainerFixture.BuildConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("name", $"\"{table}\"");
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<bool> MembershipQueriesRegisteredAsync(string database)
    {
        await using var conn = new NpgsqlConnection(PostgresContainerFixture.BuildConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM OrleansQuery WHERE QueryKey = 'MembershipReadAllKey')", conn);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
