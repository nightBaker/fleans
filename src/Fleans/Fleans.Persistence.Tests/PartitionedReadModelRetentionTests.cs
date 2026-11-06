using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Persistence.Events;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace Fleans.Persistence.Tests;

/// <summary>
/// Retention on PostgreSQL (#980): instance details live in completion-month partitions, which the
/// purge drops whole once every instance that completed in the month is purged.
/// </summary>
[TestClass]
[TestCategory("Postgres")]
public class PartitionedReadModelRetentionTests : PersistenceTestBase
{
    private static readonly Serializer Serializer =
        new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<Serializer>();

    private static readonly DateTimeOffset Cutoff = DateTimeOffset.UtcNow.AddDays(-7);
    private static readonly DateTimeOffset January = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static EfCoreEventStore CreateStore(IPersistenceTestFixture fixture) =>
        new(fixture.JournalFactory, Serializer, Options.Create(new FleansPersistenceOptions()));

    private static EfCoreWorkflowInstancePurgeService CreateService(IPersistenceTestFixture fixture) =>
        new(fixture.CommandFactory, CreateStore(fixture), NullLogger<EfCoreWorkflowInstancePurgeService>.Instance,
            retention: new PartitionedReadModelRetention(NullLogger<PartitionedReadModelRetention>.Instance));

    private static async Task EnsurePartitionsAsync(IPersistenceTestFixture fixture, DateTimeOffset now)
    {
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        await PartitionedReadModelRetention.EnsurePartitionsAsync(db, now, NullLogger.Instance);
    }

    /// <summary>An instance with a journal, a snapshot, a read-model row and details; finished unless <paramref name="completedAt"/> is null.</summary>
    private static async Task<Guid> SeedAsync(
        IPersistenceTestFixture fixture, DateTimeOffset createdAt, DateTimeOffset? completedAt, Guid? parentId = null, Guid? id = null)
    {
        var instanceId = id ?? Guid.NewGuid();
        var state = new WorkflowInstanceState();
        state.Initialize(instanceId, null, Guid.NewGuid(), createdAt);
        if (parentId is { } parent)
            state.SetParentInfo(parent, "callActivity");
        state.Start(createdAt);
        state.AddEntries([new ActivityInstanceEntry(Guid.NewGuid(), "task1", instanceId)]);
        if (completedAt is { } at)
            state.Complete(at);

        var version = completedAt is null ? 2 : 3;
        await new EfCoreWorkflowStateProjection(fixture.CommandFactory).WriteAsync(instanceId.ToString(), state, version);
        if (id is null)
        {
            var store = CreateStore(fixture);
            await store.AppendEventsAsync(instanceId, [new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()), new ExecutionStarted()], 0, Guid.NewGuid());
            await store.WriteSnapshotAsync(instanceId, 2, state);
        }
        return instanceId;
    }

    /// <summary>Instance row, entries, variable scopes, journal batches and snapshot of the instance.</summary>
    private static async Task<int[]> StoredRowsAsync(IPersistenceTestFixture fixture, Guid id)
    {
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        await using var journal = await fixture.JournalFactory.CreateDbContextAsync();
        return
        [
            await db.WorkflowInstances.CountAsync(i => i.Id == id),
            await db.WorkflowActivityInstanceEntries.CountAsync(e => e.WorkflowInstanceId == id),
            await db.WorkflowVariableStates.CountAsync(v => v.WorkflowInstanceId == id),
            await journal.WorkflowEventBatches.CountAsync(b => b.InstanceId == id),
            await journal.WorkflowSnapshots.CountAsync(s => s.InstanceId == id),
        ];
    }

    private static async Task<bool> TableExistsAsync(IPersistenceTestFixture fixture, string name)
    {
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        return await db.Database
            .SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = {0}) AS \"Value\"", name)
            .SingleAsync();
    }

    private static async Task<int> RowsInAsync(IPersistenceTestFixture fixture, string table, Guid instanceId)
    {
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        return await db.Database
            .SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM \"{table}\" WHERE \"WorkflowInstanceId\" = {{0}}", instanceId)
            .SingleAsync();
    }

    private static async Task AssertMonthPartitions(IPersistenceTestFixture fixture, DateTimeOffset month, bool exist)
    {
        foreach (var table in PartitionedReadModelRetention.Tables)
        {
            var name = PartitionedReadModelRetention.MonthPartition(table, month);
            Assert.AreEqual(exist, await TableExistsAsync(fixture, name), $"{name} exists");
        }
    }

    [TestMethod]
    public async Task Purge_DropsAMonthsPartitions_OnceEveryInstanceThatCompletedInItIsPurged()
    {
        // Arrange — two instances completed in January land in January's partitions
        await using var fixture = await TestFixtureFactory.CreateAsync(PersistenceProvider.Postgres);
        await EnsurePartitionsAsync(fixture, January.AddDays(9));
        var first = await SeedAsync(fixture, January.AddDays(13), January.AddDays(14));
        var second = await SeedAsync(fixture, January.AddDays(14), January.AddDays(15));
        Assert.AreEqual(1, await RowsInAsync(fixture,
            PartitionedReadModelRetention.MonthPartition("WorkflowActivityInstanceEntries", January), first));

        // Act
        var result = await CreateService(fixture).PurgeCompletedAsync(Cutoff);

        // Assert — everything of both instances is gone, January (and the empty months after it) dropped
        Assert.AreEqual(2, result.PurgedCount);
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0 }, await StoredRowsAsync(fixture, first));
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0 }, await StoredRowsAsync(fixture, second));
        await AssertMonthPartitions(fixture, January, exist: false);
        await AssertMonthPartitions(fixture, January.AddMonths(1), exist: false);
        var now = DateTimeOffset.UtcNow;
        await AssertMonthPartitions(fixture, new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), exist: true);
    }

    [TestMethod]
    public async Task Purge_KeepsAMonth_WhileAnInstanceThatCompletedInItIsKept()
    {
        // Arrange — a child of a still-running parent and an unrelated instance, both completed in January
        await using var fixture = await TestFixtureFactory.CreateAsync(PersistenceProvider.Postgres);
        await EnsurePartitionsAsync(fixture, January.AddDays(9));
        var parent = await SeedAsync(fixture, January.AddDays(10), completedAt: null);
        var child = await SeedAsync(fixture, January.AddDays(11), January.AddDays(14), parentId: parent);
        var other = await SeedAsync(fixture, January.AddDays(12), January.AddDays(15));

        // Act
        var first = await CreateService(fixture).PurgeCompletedAsync(Cutoff);

        // Assert — the other instance is purged but its details wait for January's partitions,
        // which the child keeps
        Assert.AreEqual(1, first.PurgedCount);
        CollectionAssert.AreEqual(new[] { 0, 1, 1, 0, 0 }, await StoredRowsAsync(fixture, other));
        CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1 }, await StoredRowsAsync(fixture, child));
        await AssertMonthPartitions(fixture, January, exist: true);

        // Act — the parent completes in January too; the next purge takes all of January
        await SeedAsync(fixture, January.AddDays(10), January.AddDays(20), id: parent);
        var second = await CreateService(fixture).PurgeCompletedAsync(Cutoff);

        // Assert
        Assert.AreEqual(2, second.PurgedCount);
        await AssertMonthPartitions(fixture, January, exist: false);
        foreach (var id in new[] { parent, child, other })
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0 }, await StoredRowsAsync(fixture, id));
    }

    [TestMethod]
    public async Task Purge_MonthWithoutPartitions_DeletesItsDetailsFromTheDefaultPartition()
    {
        // Arrange — June 2025 has no partitions, so its details sit in the default ones
        await using var fixture = await TestFixtureFactory.CreateAsync(PersistenceProvider.Postgres);
        var june = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var id = await SeedAsync(fixture, june, june.AddHours(1));
        Assert.AreEqual(1, await RowsInAsync(fixture,
            PartitionedReadModelRetention.DefaultPartition("WorkflowActivityInstanceEntries"), id));

        // Act
        var result = await CreateService(fixture).PurgeCompletedAsync(Cutoff);

        // Assert
        Assert.AreEqual(1, result.PurgedCount);
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0 }, await StoredRowsAsync(fixture, id));
    }

    [TestMethod]
    public async Task EnsurePartitions_MonthWhoseRowsAreInTheDefault_IsLeftThere()
    {
        // Arrange — a September 2025 completion went to the default partitions before September had any
        await using var fixture = await TestFixtureFactory.CreateAsync(PersistenceProvider.Postgres);
        var september = new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var id = await SeedAsync(fixture, september.AddDays(3), september.AddDays(4));

        // Act
        await EnsurePartitionsAsync(fixture, september.AddDays(5));

        // Assert — September stays in the default; the months after it get their partitions
        await AssertMonthPartitions(fixture, september, exist: false);
        await AssertMonthPartitions(fixture, september.AddMonths(1), exist: true);
        await AssertMonthPartitions(fixture, september.AddMonths(2), exist: true);
        Assert.AreEqual(1, await RowsInAsync(fixture,
            PartitionedReadModelRetention.DefaultPartition("WorkflowActivityInstanceEntries"), id));
    }
}
