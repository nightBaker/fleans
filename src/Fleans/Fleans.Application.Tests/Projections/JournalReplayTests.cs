using Fleans.Application.Projections;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Fleans.Application.Tests.Projections;

/// <summary>
/// The projector's replay skips the snapshot read while the notified version is below the snapshot
/// interval (#903): the instance has no snapshot yet, so one read of the journal is enough.
/// </summary>
[TestClass]
public class JournalReplayTests
{
    private static IEventStore Store(int snapshotInterval = 100)
    {
        var store = Substitute.For<IEventStore>();
        store.SnapshotInterval.Returns(snapshotInterval);
        store.ReadSnapshotAsync(Arg.Any<Guid>()).Returns((null, 0));
        store.ReadEventsAsync(Arg.Any<Guid>(), Arg.Any<int>()).Returns(new List<WorkflowEvent>());
        return store;
    }

    [TestMethod]
    public async Task LoadAsync_KnownVersionBelowSnapshotInterval_ReadsOnlyTheJournal()
    {
        // Arrange
        var store = Store();
        var instanceId = Guid.NewGuid();

        // Act
        var (state, version, snapshotVersion) = await JournalReplay.LoadAsync(store, instanceId, knownVersion: 5);

        // Assert
        await store.DidNotReceive().ReadSnapshotAsync(Arg.Any<Guid>());
        await store.Received(1).ReadEventsAsync(instanceId, 0);
        Assert.IsNotNull(state);
        Assert.AreEqual(0, version);
        Assert.AreEqual(0, snapshotVersion);
    }

    [TestMethod]
    public async Task LoadAsync_KnownVersionAtSnapshotInterval_ReadsTheSnapshotFirst()
    {
        // Arrange
        var store = Store();
        var instanceId = Guid.NewGuid();
        var snapshot = new WorkflowInstanceState();
        store.ReadSnapshotAsync(instanceId).Returns((snapshot, 100));

        // Act
        var (state, version, snapshotVersion) = await JournalReplay.LoadAsync(store, instanceId, knownVersion: 100);

        // Assert
        await store.Received(1).ReadSnapshotAsync(instanceId);
        await store.Received(1).ReadEventsAsync(instanceId, 100);
        Assert.AreSame(snapshot, state);
        Assert.AreEqual(100, version);
        Assert.AreEqual(100, snapshotVersion);
    }

    [TestMethod]
    public async Task LoadAsync_UnknownVersion_ReadsTheSnapshotFirst()
    {
        // Arrange — the grain's activation does not know the head
        var store = Store();
        var instanceId = Guid.NewGuid();

        // Act
        await JournalReplay.LoadAsync(store, instanceId);

        // Assert
        await store.Received(1).ReadSnapshotAsync(instanceId);
        await store.Received(1).ReadEventsAsync(instanceId, 0);
    }

    [TestMethod]
    public async Task LoadAsync_JournalTooLongForOneRead_FallsBackToTheSnapshot()
    {
        // Arrange — the head ran past MaxEventsPerLoad since the notification was sent
        var store = Store();
        var instanceId = Guid.NewGuid();
        var snapshot = new WorkflowInstanceState();
        store.ReadEventsAsync(instanceId, 0).Throws(new InvalidOperationException("Refusing to load more than 1000 events"));
        store.ReadSnapshotAsync(instanceId).Returns((snapshot, 1200));

        // Act
        var (state, version, _) = await JournalReplay.LoadAsync(store, instanceId, knownVersion: 5);

        // Assert
        Assert.AreSame(snapshot, state);
        Assert.AreEqual(1200, version);
        await store.Received(1).ReadEventsAsync(instanceId, 1200);
    }
}
