using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fleans.Persistence.Sqlite.DesignTime;

/// <summary>
/// Design-time factory for <see cref="FleanCommandDbContext"/> using the SQLite provider.
/// Used by <c>dotnet ef migrations add</c> to generate migrations in this project.
/// </summary>
public sealed class SqliteCommandDbContextFactory : IDesignTimeDbContextFactory<FleanCommandDbContext>
{
    public FleanCommandDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansSqlite("DataSource=design-time.db")
            .Options;
        return new FleanCommandDbContext(options);
    }
}
