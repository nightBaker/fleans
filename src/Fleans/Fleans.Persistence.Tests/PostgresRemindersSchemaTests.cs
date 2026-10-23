using Fleans.Persistence.Tests.Infrastructure;
using Fleans.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Fleans.Persistence.Tests;

/// <summary>
/// #886: Postgres reminders in their own database, with the (ServiceId, GrainHash) index the
/// Orleans range reads need. Runs only with <c>FLEANS_PG_TESTS=1</c> (Docker).
/// </summary>
[TestClass]
public class PostgresRemindersSchemaTests
{
    [TestMethod]
    public async Task Shared_database_gets_the_reminders_schema_next_to_the_app_tables()
    {
        if (!PostgresContainerFixture.IsEnabled)
            Assert.Inconclusive("FLEANS_PG_TESTS != 1 — skipping Postgres reminders schema test.");

        var appDb = $"fleans_shared_{Guid.NewGuid().ToString("N")[..8]}";
        await ExecuteAdminAsync($"CREATE DATABASE \"{appDb}\"");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Postgres",
                ["Fleans:Reminders:Provider"] = "Postgres",
                ["ConnectionStrings:fleans"] = PostgresContainerFixture.BuildConnectionString(appDb),
            });
            builder.AddFleansPersistence();
            using var host = builder.Build();

            await host.EnsureDatabaseSchemaAsync();
            await host.EnsureDatabaseSchemaAsync();

            Assert.IsTrue(await TableExistsAsync(appDb, "orleansreminderstable"));
            Assert.IsTrue(await TableExistsAsync(appDb, "WorkflowInstances"));
        }
        finally
        {
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{appDb}\" WITH (FORCE)");
        }
    }

    [TestMethod]
    public async Task Dedicated_reminders_database_gets_the_schema_and_range_reads_use_the_index()
    {
        if (!PostgresContainerFixture.IsEnabled)
            Assert.Inconclusive("FLEANS_PG_TESTS != 1 — skipping Postgres reminders schema test.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appDb = $"fleans_app_{suffix}";
        var remindersDb = $"fleans_reminders_{suffix}";
        await ExecuteAdminAsync($"CREATE DATABASE \"{appDb}\"");
        await ExecuteAdminAsync($"CREATE DATABASE \"{remindersDb}\"");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Postgres",
                ["Fleans:Reminders:Provider"] = "Postgres",
                ["ConnectionStrings:fleans"] = PostgresContainerFixture.BuildConnectionString(appDb),
                ["ConnectionStrings:fleans-reminders"] = PostgresContainerFixture.BuildConnectionString(remindersDb),
            });
            builder.AddFleansPersistence();
            using var host = builder.Build();

            await host.EnsureDatabaseSchemaAsync();
            await host.EnsureDatabaseSchemaAsync(); // idempotent: a second silo start is a no-op

            Assert.IsTrue(await TableExistsAsync(remindersDb, "orleansreminderstable"),
                "Reminders table must be created in the dedicated reminders database.");
            Assert.IsFalse(await TableExistsAsync(appDb, "orleansreminderstable"),
                "Reminders table must not be created in the application database.");
            Assert.IsTrue(await TableExistsAsync(appDb, "WorkflowInstances"),
                "Application migrations still run against the 'fleans' database.");

            var plan = await ExplainRangeReadAsync(remindersDb);
            StringAssert.Contains(plan.ToLowerInvariant(), "ix_reminderstable_serviceid_grainhash",
                $"Range read must use the GrainHash index. Plan:\n{plan}");
        }
        finally
        {
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{appDb}\" WITH (FORCE)");
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{remindersDb}\" WITH (FORCE)");
        }
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

    private static async Task<string> ExplainRangeReadAsync(string database)
    {
        await using var conn = new NpgsqlConnection(PostgresContainerFixture.BuildConnectionString(database));
        await conn.OpenAsync();

        // 20 000 reminders spread over the whole hash ring, then the shape of Orleans'
        // ReadRangeRows1Key query over ~0.5 % of the ring.
        const string sql = """
            INSERT INTO OrleansRemindersTable (ServiceId, GrainId, ReminderName, StartTime, Period, GrainHash, Version)
            SELECT 'fleans', 'timercallback/' || i, 'timer', now(), 60000, hashint4(i), 0
            FROM generate_series(1, 20000) AS i;
            ANALYZE OrleansRemindersTable;
            """;
        await using (var seed = new NpgsqlCommand(sql, conn))
            await seed.ExecuteNonQueryAsync();

        await using var explain = new NpgsqlCommand("""
            EXPLAIN SELECT GrainId, ReminderName, StartTime, Period, Version
            FROM OrleansRemindersTable
            WHERE ServiceId = 'fleans' AND GrainHash > 0 AND GrainHash <= 20000000
            """, conn);
        await using var reader = await explain.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }
}
