using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreMessageDeduplicationGrainStorageTests : PersistenceTestBase
{
    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task PlanThenComplete_RoundTripsTheRecord(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageDeduplicationGrainStorage(fixture.CommandFactory);
        var key = $"orderPlaced/req-{Guid.NewGuid():N}";
        var grainId = GrainId.Create("messagededuplication", key);
        var at = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var instanceId = Guid.NewGuid();
        var write = new TestGrainState<MessageDeduplicationState> { State = new MessageDeduplicationState() };
        write.State.PlanStarts([new PlannedStart("order-process", instanceId)], at);
        await storage.WriteStateAsync("state", grainId, write);

        // Act — a second write updates the row (ETag carried over)
        write.State.CompleteStarts([instanceId]);
        await storage.WriteStateAsync("state", grainId, write);
        var read = new TestGrainState<MessageDeduplicationState> { State = new MessageDeduplicationState() };
        await storage.ReadStateAsync("state", grainId, read);

        // Assert
        Assert.IsTrue(read.RecordExists);
        Assert.AreEqual(key, read.State.Key);
        Assert.AreEqual(MessageDeduplicationStatus.Started, read.State.Status);
        Assert.AreEqual(at, read.State.ReceivedAt);
        Assert.AreEqual(new PlannedStart("order-process", instanceId), read.State.PlannedStarts.Single());
        CollectionAssert.AreEqual(new[] { instanceId }, read.State.StartedInstanceIds);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_WithAStaleETag_Throws(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageDeduplicationGrainStorage(fixture.CommandFactory);
        var grainId = GrainId.Create("messagededuplication", $"orderPlaced/req-{Guid.NewGuid():N}");
        var write = new TestGrainState<MessageDeduplicationState> { State = new MessageDeduplicationState() };
        write.State.RecordCorrelated(DateTimeOffset.UtcNow);
        await storage.WriteStateAsync("state", grainId, write);
        var stale = new TestGrainState<MessageDeduplicationState> { State = write.State, ETag = "stale" };

        // Act & Assert
        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("state", grainId, stale));
    }
}
