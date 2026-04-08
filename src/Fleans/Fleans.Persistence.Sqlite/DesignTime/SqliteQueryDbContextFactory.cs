using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fleans.Persistence.Sqlite.DesignTime;

/// <summary>
/// Design-time factory for <see cref="FleanQueryDbContext"/> using the SQLite provider.
/// Used by <c>dotnet ef migrations add</c> to generate migrations in this project.
/// </summary>
public sealed class SqliteQueryDbContextFactory : IDesignTimeDbContextFactory<FleanQueryDbContext>
{
    public FleanQueryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FleanQueryDbContext>()
            .UseFleansSqlite("DataSource=design-time.db")
            .Options;
        return new FleanQueryDbContext(options);
    }
}
