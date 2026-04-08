using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Fleans.Persistence.PostgreSql.DesignTime;

/// <summary>
/// Design-time factory for <see cref="FleanCommandDbContext"/> using the PostgreSQL provider.
/// Used by <c>dotnet ef migrations add</c> to generate migrations in this project.
/// </summary>
public sealed class PostgresCommandDbContextFactory : IDesignTimeDbContextFactory<FleanCommandDbContext>
{
    public FleanCommandDbContext CreateDbContext(string[] args)
    {
        var connStr = args.Length > 0 ? args[0]
            : Environment.GetEnvironmentVariable("FLEANS_PG_DESIGN_CONNECTION")
              ?? "Host=localhost;Database=fleans_design;Username=postgres;Password=postgres";

        var dataSource = NpgsqlDataSource.Create(connStr);
        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansPostgres(dataSource)
            .Options;
        return new FleanCommandDbContext(options);
    }
}
