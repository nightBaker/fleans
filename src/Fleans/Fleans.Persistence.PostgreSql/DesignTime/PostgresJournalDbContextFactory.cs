using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Fleans.Persistence.PostgreSql.DesignTime;

/// <summary>
/// Design-time factory for <see cref="FleanJournalDbContext"/> using the PostgreSQL provider.
/// Used by <c>dotnet ef migrations add &lt;Name&gt; --context FleanJournalDbContext --output-dir Migrations/Journal</c>.
/// </summary>
public sealed class PostgresJournalDbContextFactory : IDesignTimeDbContextFactory<FleanJournalDbContext>
{
    public FleanJournalDbContext CreateDbContext(string[] args)
    {
        var connStr = args.Length > 0 ? args[0]
            : Environment.GetEnvironmentVariable("FLEANS_PG_DESIGN_CONNECTION")
              ?? "Host=localhost;Database=fleans_design;Username=postgres;Password=postgres";

        var dataSource = NpgsqlDataSource.Create(connStr);
        var options = new DbContextOptionsBuilder<FleanJournalDbContext>()
            .UseFleansPostgresJournal(dataSource)
            .Options;
        return new FleanJournalDbContext(options);
    }
}
