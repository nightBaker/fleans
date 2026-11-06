using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleans.Persistence.PostgreSql;

/// <summary>
/// Retention on PostgreSQL (#980). The three detail tables — activity entries, variable scopes,
/// condition results — are range-partitioned by <see cref="IReadModelRetention.InstanceCompletedAt"/>
/// into one partition per UTC month (<c>&lt;Table&gt;_y2026m11</c>), plus a <c>&lt;Table&gt;_default</c>
/// partition for rows of a month that has none (migration <c>PartitionReadModelDetails</c>). The
/// projector writes the details once, on completion, so no row ever moves between partitions.
/// </summary>
/// <remarks>
/// A purge batch deletes an instance's journal and instance row as before, but its details only
/// from the default partitions. A month's partitions are dropped whole once the month is over the
/// cutoff and no instance row that completed in it is left — the remaining ones (a child whose
/// parent still runs) keep the month. Until then the purged instances' details stay, unreachable:
/// every read goes through the instance row. The instance rows themselves are not partitioned: the
/// projector's upsert of a running row conflicts on <c>"Id"</c> alone, which a partitioned table
/// cannot keep unique.
/// </remarks>
public sealed partial class PartitionedReadModelRetention : IReadModelRetention
{
    /// <summary>Months after the current one that get their partitions ahead of time.</summary>
    public const int MonthsAhead = 2;

    public static readonly IReadOnlyList<string> Tables =
        ["WorkflowActivityInstanceEntries", "WorkflowVariableStates", "WorkflowConditionSequenceStates"];

    // Partition DDL waits at most this long for the tables' locks, then gives up until the next call.
    private const string LockTimeout = "5s";

    private static readonly Regex MonthSuffix = new(@"_y(\d{4})m(\d{2})$", RegexOptions.CultureInvariant);

    private readonly ILogger<PartitionedReadModelRetention> _logger;
    private readonly TimeProvider _time;

    public PartitionedReadModelRetention(ILogger<PartitionedReadModelRetention> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public static string MonthPartition(string table, DateTimeOffset month) =>
        $"{table}_y{month.Year:D4}m{month.Month:D2}";

    public static string DefaultPartition(string table) => $"{table}_default";

    public async Task DeleteDetailsAsync(FleanCommandDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var parameters = new object[] { ids.ToArray() };
        foreach (var table in Tables)
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM \"{DefaultPartition(table)}\" WHERE \"WorkflowInstanceId\" = ANY({{0}})",
                parameters, cancellationToken);
    }

    public async Task<int> ReclaimAsync(FleanCommandDbContext db, DateTimeOffset completedBefore, CancellationToken cancellationToken)
    {
        await EnsurePartitionsAsync(db, _time.GetUtcNow(), _logger, cancellationToken);

        var dropped = 0;
        foreach (var month in await ListMonthsAsync(db, cancellationToken))
        {
            if (month.AddMonths(1) <= completedBefore && await TryDropMonthAsync(db, month, cancellationToken))
                dropped++;
        }
        return dropped;
    }

    /// <summary>
    /// Creates the partitions of <paramref name="now"/>'s month and the <see cref="MonthsAhead"/>
    /// after it that are missing. Runs at startup, after the migrations, and on every purge. A month
    /// whose rows already went to the default partition gets none and stays there: the purge deletes
    /// them row by row.
    /// </summary>
    public static async Task EnsurePartitionsAsync(
        DbContext db, DateTimeOffset now, ILogger logger, CancellationToken cancellationToken = default)
    {
        var existing = (await ListMonthsAsync(db, cancellationToken)).ToHashSet();
        var current = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i <= MonthsAhead; i++)
        {
            var month = current.AddMonths(i);
            if (existing.Contains(month))
                continue;

            var from = Literal(month);
            var to = Literal(month.AddMonths(1));
            try
            {
                await InLockedTransactionAsync(db, cancellationToken, async () =>
                {
                    foreach (var table in Tables)
                        await db.Database.ExecuteSqlRawAsync(
                            $"CREATE TABLE IF NOT EXISTS \"{MonthPartition(table, month)}\" PARTITION OF \"{table}\" " +
                            $"FOR VALUES FROM ('{from}') TO ('{to}')",
                            cancellationToken);
                    return true;
                });
                LogPartitionCreated(logger, month);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation)
            {
                LogPartitionNotCreatedRowsInDefault(logger, month);
            }
            catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.DuplicateTable or PostgresErrorCodes.UniqueViolation)
            {
                // Another process created it at the same time.
            }
            catch (PostgresException e) when (IsLockConflict(e))
            {
                LogPartitionLockConflict(logger, month, e.SqlState);
            }
        }
    }

    /// <summary>
    /// Drops <paramref name="month"/>'s partitions if no instance row that completed in it is left.
    /// The parents are locked first — every query takes them before a partition — so no projector
    /// write into the month is in flight while it is checked and dropped.
    /// </summary>
    private async Task<bool> TryDropMonthAsync(FleanCommandDbContext db, DateTimeOffset month, CancellationToken cancellationToken)
    {
        var end = month.AddMonths(1);
        try
        {
            var dropped = await InLockedTransactionAsync(db, cancellationToken, async () =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"LOCK TABLE ONLY {string.Join(", ", Tables.Select(t => $"\"{t}\""))} IN ACCESS EXCLUSIVE MODE",
                    cancellationToken);
                if (await db.WorkflowInstances.AnyAsync(i => i.CompletedAt >= month && i.CompletedAt < end, cancellationToken))
                    return false;
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP TABLE IF EXISTS {string.Join(", ", Tables.Select(t => $"\"{MonthPartition(t, month)}\""))}",
                    cancellationToken);
                return true;
            });
            if (dropped)
                LogMonthDropped(_logger, month);
            else
                LogMonthKept(_logger, month);
            return dropped;
        }
        catch (PostgresException e) when (IsLockConflict(e))
        {
            LogPartitionLockConflict(_logger, month, e.SqlState);
            return false;
        }
    }

    /// <summary>The months that have partitions, read from the entries table's partitions.</summary>
    private static async Task<List<DateTimeOffset>> ListMonthsAsync(DbContext db, CancellationToken cancellationToken)
    {
        var names = await db.Database.SqlQueryRaw<string>(
                $"SELECT c.relname::text AS \"Value\" FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid " +
                $"WHERE i.inhparent = '\"{Tables[0]}\"'::regclass")
            .ToListAsync(cancellationToken);

        return names
            .Select(n => MonthSuffix.Match(n))
            .Where(m => m.Success)
            .Select(m => new DateTimeOffset(
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), 1, 0, 0, 0, TimeSpan.Zero))
            .Order()
            .ToList();
    }

    private static async Task<T> InLockedTransactionAsync<T>(DbContext db, CancellationToken cancellationToken, Func<Task<T>> body)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync($"SET LOCAL lock_timeout = '{LockTimeout}'", cancellationToken);
        var result = await body();
        await tx.CommitAsync(cancellationToken);
        return result;
    }

    private static bool IsLockConflict(PostgresException e) =>
        e.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected;

    private static string Literal(DateTimeOffset month) =>
        month.ToString("yyyy-MM-dd 00:00:00+00", CultureInfo.InvariantCulture);

    [LoggerMessage(EventId = 14104, Level = LogLevel.Information,
        Message = "Dropped the read-model detail partitions of {Month:yyyy-MM}: every instance that completed in it is purged")]
    private static partial void LogMonthDropped(ILogger logger, DateTimeOffset month);

    [LoggerMessage(EventId = 14105, Level = LogLevel.Debug,
        Message = "Kept the read-model detail partitions of {Month:yyyy-MM}: instances that completed in it are not purged yet")]
    private static partial void LogMonthKept(ILogger logger, DateTimeOffset month);

    [LoggerMessage(EventId = 14106, Level = LogLevel.Information,
        Message = "Created the read-model detail partitions of {Month:yyyy-MM}")]
    private static partial void LogPartitionCreated(ILogger logger, DateTimeOffset month);

    [LoggerMessage(EventId = 14107, Level = LogLevel.Warning,
        Message = "Read-model detail partitions of {Month:yyyy-MM} not created: its rows are already in the default partition, which the purge deletes row by row")]
    private static partial void LogPartitionNotCreatedRowsInDefault(ILogger logger, DateTimeOffset month);

    [LoggerMessage(EventId = 14108, Level = LogLevel.Warning,
        Message = "Read-model detail partitions of {Month:yyyy-MM} left as they are: their tables are busy (SQLSTATE {SqlState}); the next purge tries again")]
    private static partial void LogPartitionLockConflict(ILogger logger, DateTimeOffset month, string sqlState);
}
