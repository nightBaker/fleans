using Fleans.Application;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Persistence.Events;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace Fleans.Persistence.Tests;

/// <summary>
/// Retention purge (#862): finished instances before the cutoff lose their journal, snapshot and
/// read-model rows; everything else is untouched.
/// </summary>
[TestClass]
[TestCategory("Postgres")]
public class EfCoreWorkflowInstancePurgeServiceTests : PersistenceTestBase
{
    private static readonly Serializer Serializer =
        new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<Serializer>();

    private static readonly DateTimeOffset Cutoff = DateTimeOffset.UtcNow.AddDays(-7);
    private static readonly DateTimeOffset LongAgo = Cutoff.AddDays(-3);

    private static EfCoreEventStore CreateStore(IPersistenceTestFixture fixture) =>
        new(fixture.CommandFactory, Serializer, Options.Create(new FleansPersistenceOptions()));

    private static EfCoreWorkflowInstancePurgeService CreateService(IPersistenceTestFixture fixture) =>
        new(fixture.CommandFactory, CreateStore(fixture), NullLogger<EfCoreWorkflowInstancePurgeService>.Instance);

    private enum Outcome { Running, Completed, Cancelled, Failed }

    /// <summary>
    /// An instance with every kind of stored data: journal, snapshot, read-model row and details,
    /// and a user task.
    /// </summary>
    private static async Task<Guid> SeedAsync(
        IPersistenceTestFixture fixture, Outcome outcome, DateTimeOffset finishedAt, Guid? parentId = null)
    {
        var id = Guid.NewGuid();
        var state = new WorkflowInstanceState();
        state.Initialize(id, null, Guid.NewGuid(), finishedAt.AddMinutes(-5));
        if (parentId is { } parent)
            state.SetParentInfo(parent, "callActivity");
        state.Start(finishedAt.AddMinutes(-5));
        state.AddEntries([new ActivityInstanceEntry(Guid.NewGuid(), "task1", id)]);
        switch (outcome)
        {
            case Outcome.Completed: state.Complete(finishedAt); break;
            case Outcome.Cancelled: state.Cancel(finishedAt); break;
            case Outcome.Failed: state.Fail(finishedAt); break;
        }

        await new EfCoreWorkflowStateProjection(fixture.CommandFactory).WriteAsync(id.ToString(), state);
        var store = CreateStore(fixture);
        await store.AppendEventsAsync(id, [new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()), new ExecutionStarted()], 0, Guid.NewGuid());
        await store.AppendEventsAsync(id, [new WorkflowCompleted()], 2, Guid.NewGuid());
        await store.WriteSnapshotAsync(id, 3, state);

        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        db.UserTasks.Add(new UserTaskState
        {
            ActivityInstanceId = Guid.NewGuid(),
            WorkflowInstanceId = id,
            ActivityId = "task1",
            CreatedAt = finishedAt,
            ETag = "etag"
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Rows stored for the instance, per table — all zero once purged.</summary>
    private static async Task<int[]> StoredRowsAsync(IPersistenceTestFixture fixture, Guid id)
    {
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        return
        [
            await db.WorkflowInstances.CountAsync(i => i.Id == id),
            await db.WorkflowActivityInstanceEntries.CountAsync(e => e.WorkflowInstanceId == id),
            await db.WorkflowVariableStates.CountAsync(v => v.WorkflowInstanceId == id),
            await db.UserTasks.CountAsync(t => t.WorkflowInstanceId == id),
            await db.WorkflowEventBatches.CountAsync(b => b.InstanceId == id),
            await db.WorkflowSnapshots.CountAsync(s => s.InstanceId == id),
        ];
    }

    private static async Task AssertPurged(IPersistenceTestFixture fixture, Guid id) =>
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0, 0 }, await StoredRowsAsync(fixture, id), $"instance {id} not purged");

    private static async Task AssertKept(IPersistenceTestFixture fixture, Guid id)
    {
        var rows = await StoredRowsAsync(fixture, id);
        Assert.AreEqual(1, rows[0], $"instance {id} row deleted");
        Assert.AreEqual(1, rows[3], $"instance {id} user task deleted");
        Assert.AreEqual(2, rows[4], $"instance {id} journal deleted");
        Assert.AreEqual(1, rows[5], $"instance {id} snapshot deleted");
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Purge_FinishedBeforeCutoff_DeletesJournalSnapshotAndReadModel(PersistenceProvider provider)
    {
        // Arrange — completed, cancelled and failed all count as finished
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var completed = await SeedAsync(fixture, Outcome.Completed, LongAgo);
        var cancelled = await SeedAsync(fixture, Outcome.Cancelled, LongAgo);
        var failed = await SeedAsync(fixture, Outcome.Failed, LongAgo);
        Assert.AreEqual(1, (await StoredRowsAsync(fixture, completed))[1], "seeded entries");

        // Act
        var result = await CreateService(fixture).PurgeCompletedAsync(Cutoff);

        // Assert
        Assert.AreEqual(new WorkflowInstancePurgeResult(3, HasMore: false), result);
        await AssertPurged(fixture, completed);
        await AssertPurged(fixture, cancelled);
        await AssertPurged(fixture, failed);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Purge_KeepsRunningInstances_AndThoseFinishedAfterCutoff(PersistenceProvider provider)
    {
        // Arrange — a long-running instance started before the cutoff, and a recent completion
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var running = await SeedAsync(fixture, Outcome.Running, LongAgo);
        var recent = await SeedAsync(fixture, Outcome.Completed, Cutoff.AddDays(2));
        var old = await SeedAsync(fixture, Outcome.Completed, LongAgo);

        // Act
        var result = await CreateService(fixture).PurgeCompletedAsync(Cutoff);

        // Assert
        Assert.AreEqual(1, result.PurgedCount);
        await AssertPurged(fixture, old);
        await AssertKept(fixture, running);
        await AssertKept(fixture, recent);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Purge_KeepsChildOfRunningParent_PurgesChildOfFinishedParent(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var runningParent = await SeedAsync(fixture, Outcome.Running, LongAgo);
        var childOfRunning = await SeedAsync(fixture, Outcome.Completed, LongAgo, parentId: runningParent);
        var finishedParent = await SeedAsync(fixture, Outcome.Completed, LongAgo);
        var childOfFinished = await SeedAsync(fixture, Outcome.Completed, LongAgo, parentId: finishedParent);

        // Act
        var result = await CreateService(fixture).PurgeCompletedAsync(Cutoff);

        // Assert
        Assert.AreEqual(2, result.PurgedCount);
        await AssertKept(fixture, runningParent);
        await AssertKept(fixture, childOfRunning);
        await AssertPurged(fixture, finishedParent);
        await AssertPurged(fixture, childOfFinished);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Purge_InBatches_StopsAtMaxInstances_OldestFirst(PersistenceProvider provider)
    {
        // Arrange — five finished instances, one day apart
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var ids = new List<Guid>();
        for (var day = 5; day >= 1; day--)
            ids.Add(await SeedAsync(fixture, Outcome.Completed, Cutoff.AddDays(-day)));
        var service = CreateService(fixture);

        // Act — two batches of two, then a partial batch of one, capped at three
        var first = await service.PurgeCompletedAsync(Cutoff, batchSize: 2, maxInstances: 3);

        // Assert — the three oldest are gone; more remain
        Assert.AreEqual(new WorkflowInstancePurgeResult(3, HasMore: true), first);
        foreach (var id in ids.Take(3))
            await AssertPurged(fixture, id);
        foreach (var id in ids.Skip(3))
            await AssertKept(fixture, id);

        // Act — the next call finishes the rest
        var second = await service.PurgeCompletedAsync(Cutoff, batchSize: 2, maxInstances: 3);

        // Assert
        Assert.AreEqual(new WorkflowInstancePurgeResult(2, HasMore: false), second);
        foreach (var id in ids)
            await AssertPurged(fixture, id);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Purge_ReadModelLeftBehindByAnInterruptedPurge_IsFinishedByTheNextCall(PersistenceProvider provider)
    {
        // Arrange — the journal was deleted, the purge stopped before the read model
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var id = await SeedAsync(fixture, Outcome.Completed, LongAgo);
        await CreateStore(fixture).DeleteInstanceAsync(id);

        // Act
        var result = await CreateService(fixture).PurgeCompletedAsync(Cutoff);

        // Assert
        Assert.AreEqual(1, result.PurgedCount);
        await AssertPurged(fixture, id);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Purge_InvalidArguments_ThrowAndDeleteNothing(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var recent = await SeedAsync(fixture, Outcome.Completed, DateTimeOffset.UtcNow.AddMinutes(-10));
        var service = CreateService(fixture);

        // Act / Assert — a cutoff newer than the minimum age could hit instances still in memory
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => service.PurgeCompletedAsync(DateTimeOffset.UtcNow));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => service.PurgeCompletedAsync(Cutoff, batchSize: 0));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => service.PurgeCompletedAsync(Cutoff, batchSize: IWorkflowInstancePurgeService.MaxBatchSize + 1));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => service.PurgeCompletedAsync(Cutoff, maxInstances: 0));
        await AssertKept(fixture, recent);
    }
}
