using Fleans.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Per-row fixture for <see cref="PersistenceProvider.Sqlite"/>. Owns an in-memory
/// SQLite connection that is held open for the lifetime of the fixture so EF Core sees a
/// stable schema across DbContexts.
/// </summary>
internal sealed class SqliteRowFixture : IPersistenceTestFixture
{
    private readonly SqliteConnection _connection;

    private SqliteRowFixture(
        SqliteConnection connection,
        IDbContextFactory<FleanCommandDbContext> commandFactory,
        IDbContextFactory<FleanQueryDbContext> queryFactory)
    {
        _connection = connection;
        CommandFactory = commandFactory;
        QueryFactory = queryFactory;
    }

    public PersistenceProvider Provider => PersistenceProvider.Sqlite;

    public IDbContextFactory<FleanCommandDbContext> CommandFactory { get; }

    public IDbContextFactory<FleanQueryDbContext> QueryFactory { get; }

    public static async Task<IPersistenceTestFixture> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var commandOptions = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansSqlite(connection)
            .Options;
        var queryOptions = new DbContextOptionsBuilder<FleanQueryDbContext>()
            .UseFleansSqlite(connection)
            .Options;

        var commandFactory = new TestDbContextFactory(commandOptions);
        var queryFactory = new TestQueryDbContextFactory(queryOptions);

        await using (var ctx = await commandFactory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        return new SqliteRowFixture(connection, commandFactory, queryFactory);
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
