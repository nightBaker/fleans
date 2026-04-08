using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Fleans.Persistence.PostgreSql.DesignTime;

/// <summary>
/// Design-time factory for <see cref="FleanQueryDbContext"/> using the PostgreSQL provider.
/// Used by <c>dotnet ef migrations add</c> to generate migrations in this project.
/// </summary>
public sealed class PostgresQueryDbContextFactory : IDesignTimeDbContextFactory<FleanQueryDbContext>
{
    public FleanQueryDbContext CreateDbContext(string[] args)
    {
        var connStr = args.Length > 0 ? args[0]
            : Environment.GetEnvironmentVariable("FLEANS_PG_DESIGN_CONNECTION")
              ?? "Host=localhost;Database=fleans_design;Username=postgres;Password=postgres";

        var dataSource = NpgsqlDataSource.Create(connStr);
        var options = new DbContextOptionsBuilder<FleanQueryDbContext>()
            .UseFleansPostgres(dataSource)
            .Options;
        return new FleanQueryDbContext(options);
    }
}
