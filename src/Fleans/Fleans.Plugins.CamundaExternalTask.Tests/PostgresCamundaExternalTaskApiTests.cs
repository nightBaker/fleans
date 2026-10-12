using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

/// <summary>
/// Re-runs every <see cref="CamundaExternalTaskApiTests"/> case against PostgreSQL (schema DDL,
/// conditional lock UPDATE, date filters). Requires Docker and <c>FLEANS_PG_TESTS=1</c>.
/// </summary>
[TestClass]
[TestCategory("Postgres")]
public class PostgresCamundaExternalTaskApiTests : CamundaExternalTaskApiTests
{
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
    {
        var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        return container;
    });

    protected override async Task<Action<DbContextOptionsBuilder>> CreateDatabase()
    {
        if (Environment.GetEnvironmentVariable("FLEANS_PG_TESTS") != "1")
            Assert.Inconclusive("Set FLEANS_PG_TESTS=1 (Docker required) to run PostgreSQL tests.");

        var container = await Container.Value;
        // A fresh database per test; the schema initializer creates it.
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = "camunda_" + Guid.NewGuid().ToString("N"),
        }.ConnectionString;
        return o => o.UseNpgsql(connectionString);
    }
}
