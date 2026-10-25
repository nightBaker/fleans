using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Counts what a piece of persistence code costs the database: every command EF executes, and every
/// write transaction — an explicit transaction committed, or a command that wrote outside one
/// (autocommit). A <c>SELECT</c> outside a transaction is a read and writes no commit record.
/// </summary>
internal sealed class CommandCounter : DbCommandInterceptor, IDbTransactionInterceptor
{
    private int _commands;
    private int _writeTransactions;

    public int Commands => _commands;
    public int WriteTransactions => _writeTransactions;

    public void Reset()
    {
        _commands = 0;
        _writeTransactions = 0;
    }

    /// <summary>A command factory over <paramref name="fixture"/>'s database that reports to this counter.</summary>
    public IDbContextFactory<FleanCommandDbContext> Wrap(IPersistenceTestFixture fixture) =>
        new TestDbContextFactory(new DbContextOptionsBuilder<FleanCommandDbContext>(
                ((TestDbContextFactory)fixture.CommandFactory).Options)
            .AddInterceptors(this)
            .Options);

    private void Count(DbCommand command)
    {
        Interlocked.Increment(ref _commands);
        var isRead = command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
        if (command.Transaction is null && !isRead)
            Interlocked.Increment(ref _writeTransactions);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _writeTransactions);
        return Task.CompletedTask;
    }
}
