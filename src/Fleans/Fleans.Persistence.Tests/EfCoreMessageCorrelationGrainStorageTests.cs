using Fleans.Domain.States;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EfCoreMessageCorrelationGrainStorageTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreMessageCorrelationGrainStorage _storage = null!;
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
        _storage = new EfCoreMessageCorrelationGrainStorage(_dbContextFactory);

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
        var grainId = NewGrainId("paymentReceived");
        var state = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["order-123"] = new(Guid.NewGuid(), "waitPayment", Guid.NewGuid()),
            ["order-456"] = new(Guid.NewGuid(), "waitConfirm", Guid.NewGuid())
        });

        await _storage.WriteStateAsync(StateName, grainId, state);
        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(2, readState.State.Subscriptions.Count);
        Assert.IsTrue(readState.State.Subscriptions.ContainsKey("order-123"));
        Assert.IsTrue(readState.State.Subscriptions.ContainsKey("order-456"));
        Assert.AreEqual("waitPayment", readState.State.Subscriptions["order-123"].ActivityId);
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_PreservesSubscriptionDetails()
    {
        var instanceId = Guid.NewGuid();
        var hostActivityInstanceId = Guid.NewGuid();
        var grainId = NewGrainId("orderCancelled");
        var state = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["corr-key-1"] = new(instanceId, "activity-1", hostActivityInstanceId)
        });

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var sub = readState.State.Subscriptions["corr-key-1"];
        Assert.AreEqual(instanceId, sub.WorkflowInstanceId);
        Assert.AreEqual("activity-1", sub.ActivityId);
        Assert.AreEqual(hostActivityInstanceId, sub.HostActivityInstanceId);
    }

    [TestMethod]
    public async Task Write_WithCorrectETag_Succeeds()
    {
        var grainId = NewGrainId("msg1");
        var state = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["key1"] = new(Guid.NewGuid(), "act1", Guid.NewGuid())
        });
        await _storage.WriteStateAsync(StateName, grainId, state);
        var firstETag = state.ETag;

        state.State.Subscriptions["key2"] = new(Guid.NewGuid(), "act2", Guid.NewGuid());
        await _storage.WriteStateAsync(StateName, grainId, state);

        Assert.AreNotEqual(firstETag, state.ETag);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.AreEqual(2, readState.State.Subscriptions.Count);
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId("staleMsg");
        var state = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["key1"] = new(Guid.NewGuid(), "act1", Guid.NewGuid())
        });
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Subscriptions["key2"] = new(Guid.NewGuid(), "act2", Guid.NewGuid());
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        state.State.Subscriptions["key3"] = new(Guid.NewGuid(), "act3", Guid.NewGuid());
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Clear_RemovesState_SubsequentReadReturnsDefault()
    {
        var grainId = NewGrainId("clearMsg");
        var state = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["key1"] = new(Guid.NewGuid(), "act1", Guid.NewGuid())
        });
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
        var state = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["key1"] = new(Guid.NewGuid(), "act1", Guid.NewGuid())
        });
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Subscriptions["key2"] = new(Guid.NewGuid(), "act2", Guid.NewGuid());
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
        var grainId1 = NewGrainId("msgA");
        var grainId2 = NewGrainId("msgB");

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var state1 = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["keyA"] = new(id1, "actA", Guid.NewGuid())
        });
        var state2 = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["keyB"] = new(id2, "actB", Guid.NewGuid())
        });

        await _storage.WriteStateAsync(StateName, grainId1, state1);
        await _storage.WriteStateAsync(StateName, grainId2, state2);

        var read1 = CreateEmptyGrainState();
        var read2 = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId1, read1);
        await _storage.ReadStateAsync(StateName, grainId2, read2);

        Assert.AreEqual(1, read1.State.Subscriptions.Count);
        Assert.AreEqual(id1, read1.State.Subscriptions["keyA"].WorkflowInstanceId);
        Assert.AreEqual(1, read2.State.Subscriptions.Count);
        Assert.AreEqual(id2, read2.State.Subscriptions["keyB"].WorkflowInstanceId);
    }

    [TestMethod]
    public async Task WriteClearWrite_ReCreatesSameGrainId()
    {
        var grainId = NewGrainId("recreate");
        var state = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["key1"] = new(Guid.NewGuid(), "act1", Guid.NewGuid())
        });
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);

        var newState = CreateGrainState(new Dictionary<string, MessageSubscription>
        {
            ["key2"] = new(Guid.NewGuid(), "act2", Guid.NewGuid())
        });
        await _storage.WriteStateAsync(StateName, grainId, newState);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(1, readState.State.Subscriptions.Count);
        Assert.IsTrue(readState.State.Subscriptions.ContainsKey("key2"));
        Assert.IsTrue(readState.RecordExists);
    }

    private static GrainId NewGrainId(string key)
        => GrainId.Create("messagecorrelation", key);

    private static TestGrainState<MessageCorrelationState> CreateGrainState(
        Dictionary<string, MessageSubscription> subscriptions)
    {
        var state = new TestGrainState<MessageCorrelationState> { State = new MessageCorrelationState() };
        foreach (var kvp in subscriptions)
            state.State.Subscriptions[kvp.Key] = kvp.Value;
        return state;
    }

    private static TestGrainState<MessageCorrelationState> CreateEmptyGrainState()
        => new()
        {
            State = new MessageCorrelationState()
        };
}
