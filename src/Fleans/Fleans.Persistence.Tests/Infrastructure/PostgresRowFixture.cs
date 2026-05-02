using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Per-row fixture for <see cref="PersistenceProvider.Postgres"/>. Reads the per-class
/// connection from <see cref="PersistenceTestBase"/> static state and wraps it in a
/// disposable that exposes the EF factories. Connection pool resources owned by the
/// underlying <see cref="NpgsqlDataSource"/> on <see cref="PersistenceTestBase"/> are
/// released by <see cref="PersistenceTestBase.CleanupClassAsync"/>.
/// </summary>
internal sealed class PostgresRowFixture : IPersistenceTestFixture
{
    private PostgresRowFixture(
        IDbContextFactory<FleanCommandDbContext> commandFactory,
        IDbContextFactory<FleanQueryDbContext> queryFactory)
    {
        CommandFactory = commandFactory;
        QueryFactory = queryFactory;
    }

    public PersistenceProvider Provider => PersistenceProvider.Postgres;

    public IDbContextFactory<FleanCommandDbContext> CommandFactory { get; }

    public IDbContextFactory<FleanQueryDbContext> QueryFactory { get; }

    public static Task<IPersistenceTestFixture> CreateAsync()
    {
        if (PersistenceTestBase.PgCommandDataSource is null)
        {
            throw new InvalidOperationException(
                "PostgresRowFixture requires PersistenceTestBase.InitClassAsync to have run; " +
                "ensure the test class extends PersistenceTestBase.");
        }

        var commandOptions = PersistenceTestBase.BuildCommandOptions(PersistenceTestBase.PgCommandDataSource);
        var queryOptions = PersistenceTestBase.BuildQueryOptions(PersistenceTestBase.PgCommandDataSource);
        var commandFactory = new TestDbContextFactory(commandOptions);
        var queryFactory = new TestQueryDbContextFactory(queryOptions);
        IPersistenceTestFixture fixture = new PostgresRowFixture(commandFactory, queryFactory);
        return Task.FromResult(fixture);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
