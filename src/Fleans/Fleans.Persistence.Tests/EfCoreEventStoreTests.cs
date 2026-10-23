using System.Data.Common;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Persistence.Events;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Fleans.Persistence.Tests;

/// <summary>
/// How <see cref="EfCoreEventStore"/> lays the journal out, on the default layout (the journal in
/// the main database). Its <see cref="IEventStore"/> semantics are in
/// <see cref="EventStoreContractTests"/> (<see cref="EfCoreSqliteEventStoreContractTests"/> and the
/// PostgreSQL / Citus runs).
/// </summary>
[TestClass]
[TestCategory("Postgres")]
public class EfCoreEventStoreTests : PersistenceTestBase
{
    private static EfCoreEventStore CreateStore(IPersistenceTestFixture fixture) =>
        EfCoreEventStoreFactory.Create(fixture.JournalFactory, maxEventsPerLoad: 1000);

    [TestMethod]
    public void Default_MaxEventsPerLoad_Is1000()
    {
        var opts = new FleansPersistenceOptions();
        Assert.AreEqual(1000, opts.MaxEventsPerLoad);
    }

    [TestMethod]
    public void Default_SnapshotInterval_Is100()
    {
        Assert.AreEqual(100, new FleansPersistenceOptions().SnapshotInterval);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task AppendEvents_OneRowPerAppend_KeepsOccurredAt(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);
        var grainId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await store.AppendEventsAsync(grainId,
            [new WorkflowStarted(grainId, "p1", Guid.NewGuid()), new ExecutionStarted { OccurredAt = occurredAt }],
            expectedVersion: 0, batchId: Guid.NewGuid());
        await store.AppendEventsAsync(grainId, [new WorkflowCompleted()], expectedVersion: 2, batchId: Guid.NewGuid());

        var loaded = await store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(occurredAt, ((ExecutionStarted)loaded[1]).OccurredAt);
        await using var db = await fixture.JournalFactory.CreateDbContextAsync();
        var rows = await db.WorkflowEventBatches.AsNoTracking()
            .Where(b => b.InstanceId == grainId).OrderBy(b => b.FirstVersion).ToListAsync();
        Assert.HasCount(2, rows);
        Assert.AreEqual((1, 2), (rows[0].FirstVersion, rows[0].LastVersion));
        Assert.AreEqual((3, 3), (rows[1].FirstVersion, rows[1].LastVersion));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task AppendEvents_RetryOfAStoredBatch_KeepsOneRowWithItsBatchId(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);
        var grainId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        List<WorkflowEvent> batch = [new ExecutionStarted(), new WorkflowCompleted()];
        await store.AppendEventsAsync(grainId, [new ExecutionStarted()], expectedVersion: 0, batchId: Guid.NewGuid());
        await store.AppendEventsAsync(grainId, batch, expectedVersion: 1, batchId: batchId);

        Assert.IsTrue(await store.AppendEventsAsync(grainId, batch, expectedVersion: 1, batchId: batchId));

        await using var db = await fixture.JournalFactory.CreateDbContextAsync();
        var rows = await db.WorkflowEventBatches.AsNoTracking()
            .Where(b => b.InstanceId == grainId).OrderBy(b => b.FirstVersion).ToListAsync();
        Assert.HasCount(2, rows);
        Assert.AreEqual(batchId, rows[1].BatchId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadSnapshot_UnreadablePayload_Throws(PersistenceProvider provider)
    {
        // An unreadable snapshot must not be returned as state.
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);
        var grainId = Guid.NewGuid();
        await using (var db = await fixture.JournalFactory.CreateDbContextAsync())
        {
            db.WorkflowSnapshots.Add(new WorkflowSnapshotEntity
            {
                InstanceId = grainId, Version = 3, Payload = [1, 2, 3], Timestamp = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var threw = false;
        try { await store.ReadSnapshotAsync(grainId); }
        catch (Exception) { threw = true; }
        Assert.IsTrue(threw, "an unreadable snapshot must not be returned as state");
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task EveryJournalCommand_IsKeyedByInstanceId(PersistenceProvider provider)
    {
        // The shard-key invariant (#879): every command the store sends filters or keys by
        // InstanceId, so a store distributed by it (Citus) routes each one to a single shard.
        // A new query across instances — a scan, a join, a cleanup by date — fails here.
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var recorder = new CommandRecorder();
        var store = EfCoreEventStoreFactory.Create(new RecordingJournalFactory(fixture.JournalFactory, recorder), 1000);
        var id = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var state = new WorkflowInstanceState();
        state.Initialize(id, null, Guid.NewGuid(), DateTimeOffset.UtcNow);

        await store.AppendEventsAsync(id, [new ExecutionStarted()], expectedVersion: 0, batchId: batchId);
        await store.AppendEventsAsync(id, [new ExecutionStarted()], expectedVersion: 0, batchId: batchId); // retry
        await store.AppendEventsAsync(id, [new WorkflowCompleted()], expectedVersion: 0, batchId: Guid.NewGuid()); // conflict
        await store.ReadEventsAsync(id, afterVersion: 0);
        await store.WriteSnapshotAsync(id, 1, state); // insert
        await store.WriteSnapshotAsync(id, 1, state); // update
        await store.ReadSnapshotAsync(id);

        Assert.IsGreaterThanOrEqualTo(7, recorder.Commands.Count);
        foreach (var sql in recorder.Commands)
        {
            var isInsert = sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(isInsert ? sql.Contains("\"InstanceId\"") : sql.Contains("\"InstanceId\" ="),
                $"journal command not keyed by InstanceId:\n{sql}");
            Assert.IsFalse(sql.Contains(" JOIN ", StringComparison.OrdinalIgnoreCase), $"journal command joins:\n{sql}");
        }
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingJournalFactory(IDbContextFactory<FleanJournalDbContext> inner, CommandRecorder recorder)
        : IDbContextFactory<FleanJournalDbContext>
    {
        public FleanJournalDbContext CreateDbContext()
        {
            using var template = inner.CreateDbContext();
            var options = new DbContextOptionsBuilder<FleanJournalDbContext>(
                    (DbContextOptions<FleanJournalDbContext>)template.GetService<IDbContextOptions>())
                .AddInterceptors(recorder)
                .Options;
            return new FleanJournalDbContext(options);
        }
    }
}
