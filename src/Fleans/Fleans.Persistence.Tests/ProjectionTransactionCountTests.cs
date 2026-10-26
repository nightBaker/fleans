using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence.Tests;

/// <summary>
/// What projecting many instances costs the read-model database: commands and write transactions
/// for a typical instance's three projections (created, started, completed).
/// </summary>
[TestClass]
[TestCategory("Postgres")]
public class ProjectionTransactionCountTests : PersistenceTestBase
{
    private const int Instances = 64;
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    /// <summary>The three projections of each instance: its row created, started, then completed with details.</summary>
    private static List<List<(Guid Id, WorkflowInstanceState State, int Version)>> Stages()
    {
        var created = new List<(Guid, WorkflowInstanceState, int)>();
        var started = new List<(Guid, WorkflowInstanceState, int)>();
        var completed = new List<(Guid, WorkflowInstanceState, int)>();
        for (var i = 0; i < Instances; i++)
        {
            var id = Guid.NewGuid();
            var definition = Guid.NewGuid();

            var a = new WorkflowInstanceState();
            a.Initialize(id, null, definition, At, businessId: $"order-{i}");
            created.Add((id, a, 1));

            var b = new WorkflowInstanceState();
            b.Initialize(id, null, definition, At, businessId: $"order-{i}");
            b.Start(At);
            started.Add((id, b, 2));

            var c = new WorkflowInstanceState();
            c.Initialize(id, null, definition, At, businessId: $"order-{i}");
            c.Start(At);
            c.AddEntries([new ActivityInstanceEntry(Guid.NewGuid(), "task1", id)]);
            c.CompleteEntry(c.Entries.Single().ActivityInstanceId, At);
            c.Complete(At);
            completed.Add((id, c, 3));
        }
        return [created, started, completed];
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Unbatched_EachProjectionIsItsOwnWriteTransaction(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var counter = new CommandCounter();
        var projection = new EfCoreWorkflowStateProjection(counter.Wrap(fixture));

        // Act — one write per projection (Fleans:Projector:MaxBatchSize = 1)
        foreach (var stage in Stages())
            foreach (var (id, state, version) in stage)
                await projection.WriteAsync(id.ToString(), state, version);

        // Assert
        Report(provider, "unbatched", counter);
        Assert.AreEqual(Instances * 3, counter.WriteTransactions);
        await AssertAllCompletedAsync(fixture);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Batched_OneWriteTransactionPerBatch(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var counter = new CommandCounter();
        var projection = new EfCoreWorkflowStateProjection(counter.Wrap(fixture));

        // Act — each stage's projections of all instances as one batch, as the batcher groups them
        foreach (var stage in Stages())
            await projection.WriteBatchAsync(stage.Select(w => new ProjectionWrite(w.Id.ToString(), w.State, w.Version)).ToList());

        // Assert — one upsert per running batch; the completing batch: one split query (4 reads)
        // and one SaveChanges
        Report(provider, "batched", counter);
        Assert.AreEqual(3, counter.WriteTransactions);
        await AssertAllCompletedAsync(fixture);
    }

    private void Report(PersistenceProvider provider, string mode, CommandCounter counter) =>
        TestContext.WriteLine(
            $"{provider} {mode}: {counter.Commands} commands, {counter.WriteTransactions} write transactions " +
            $"for {Instances} instances x 3 projections = {(double)counter.WriteTransactions / Instances:0.###} write transactions per instance");

    private static async Task AssertAllCompletedAsync(IPersistenceTestFixture fixture)
    {
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        Assert.AreEqual(Instances, await db.WorkflowInstances.CountAsync(s => s.IsCompleted));
        Assert.AreEqual(Instances, await db.WorkflowActivityInstanceEntries.CountAsync());
    }
}
