using Fleans.Domain.States;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EfCoreSignalCorrelationGrainStorageTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreSignalCorrelationGrainStorage _storage = null!;
    private const string StateName = "state";

    [TestInitialize]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);
        _storage = new EfCoreSignalCorrelationGrainStorage(_dbContextFactory);

        using var db = _dbContextFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_ReturnsStoredState()
    {
        var grainId = NewGrainId("approvalSignal");
        var sub1 = new SignalSubscription(Guid.NewGuid(), "waitApproval", Guid.NewGuid());
        var sub2 = new SignalSubscription(Guid.NewGuid(), "waitConfirm", Guid.NewGuid());
        var state = CreateGrainState(sub1, sub2);

        await _storage.WriteStateAsync(StateName, grainId, state);
        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(2, readState.State.Subscriptions.Count);
        Assert.IsTrue(readState.State.Subscriptions.Any(s => s.ActivityId == "waitApproval"));
        Assert.IsTrue(readState.State.Subscriptions.Any(s => s.ActivityId == "waitConfirm"));
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_PreservesSubscriptionDetails()
    {
        var instanceId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var grainId = NewGrainId("detailSignal");
        var state = CreateGrainState(
            new SignalSubscription(instanceId, "activity-1", hostId));

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var sub = readState.State.Subscriptions[0];
        Assert.AreEqual(instanceId, sub.WorkflowInstanceId);
        Assert.AreEqual("activity-1", sub.ActivityId);
        Assert.AreEqual(hostId, sub.HostActivityInstanceId);
        Assert.AreEqual("detailSignal", sub.SignalName);
    }

    [TestMethod]
    public async Task Write_WithCorrectETag_Succeeds()
    {
        var grainId = NewGrainId("sig1");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, state);
        var firstETag = state.ETag;

        state.State.Subscriptions.Add(new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, state);

        Assert.AreNotEqual(firstETag, state.ETag);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.AreEqual(2, readState.State.Subscriptions.Count);
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId("staleSig");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Subscriptions.Add(new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        state.State.Subscriptions.Add(new SignalSubscription(Guid.NewGuid(), "act3", Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Write_DiffRemovesSubscription()
    {
        var grainId = NewGrainId("diffRemove");
        var sub1 = new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid());
        var sub2 = new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid());
        var state = CreateGrainState(sub1, sub2);
        await _storage.WriteStateAsync(StateName, grainId, state);

        state.State.Subscriptions.Remove(sub1);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.AreEqual(1, readState.State.Subscriptions.Count);
        Assert.AreEqual("act2", readState.State.Subscriptions[0].ActivityId);
    }

    [TestMethod]
    public async Task Clear_RemovesState_SubsequentReadReturnsDefault()
    {
        var grainId = NewGrainId("clearSig");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);
        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsNull(readState.ETag);
        Assert.IsFalse(readState.RecordExists);
    }

    [TestMethod]
    public async Task Clear_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId("clearStale");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Subscriptions.Add(new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.ClearStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Clear_NonExistentGrain_IsNoOp()
    {
        var grainId = NewGrainId("noop");
        var state = CreateEmptyGrainState();

        await _storage.ClearStateAsync(StateName, grainId, state);

        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [TestMethod]
    public async Task Read_NonExistentKey_LeavesStateUnchanged()
    {
        var grainId = NewGrainId("missing");
        var state = CreateEmptyGrainState();

        await _storage.ReadStateAsync(StateName, grainId, state);

        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [TestMethod]
    public async Task DifferentGrainIds_AreIsolated()
    {
        var grainId1 = NewGrainId("sigA");
        var grainId2 = NewGrainId("sigB");

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var state1 = CreateGrainState(
            new SignalSubscription(id1, "actA", Guid.NewGuid()));
        var state2 = CreateGrainState(
            new SignalSubscription(id2, "actB", Guid.NewGuid()));

        await _storage.WriteStateAsync(StateName, grainId1, state1);
        await _storage.WriteStateAsync(StateName, grainId2, state2);

        var read1 = CreateEmptyGrainState();
        var read2 = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId1, read1);
        await _storage.ReadStateAsync(StateName, grainId2, read2);

        Assert.AreEqual(1, read1.State.Subscriptions.Count);
        Assert.AreEqual(id1, read1.State.Subscriptions[0].WorkflowInstanceId);
        Assert.AreEqual(1, read2.State.Subscriptions.Count);
        Assert.AreEqual(id2, read2.State.Subscriptions[0].WorkflowInstanceId);
    }

    [TestMethod]
    public async Task WriteClearWrite_ReCreatesSameGrainId()
    {
        var grainId = NewGrainId("recreate");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);

        var newState = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, newState);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(1, readState.State.Subscriptions.Count);
        Assert.AreEqual("act2", readState.State.Subscriptions[0].ActivityId);
        Assert.IsTrue(readState.RecordExists);
    }

    private static GrainId NewGrainId(string key)
        => GrainId.Create("signalcorrelation", key);

    private static TestGrainState<SignalCorrelationState> CreateGrainState(
        params SignalSubscription[] subscriptions)
    {
        var state = new TestGrainState<SignalCorrelationState> { State = new SignalCorrelationState() };
        foreach (var sub in subscriptions)
            state.State.Subscriptions.Add(sub);
        return state;
    }

    private static TestGrainState<SignalCorrelationState> CreateEmptyGrainState()
        => new()
        {
            State = new SignalCorrelationState()
        };
}
