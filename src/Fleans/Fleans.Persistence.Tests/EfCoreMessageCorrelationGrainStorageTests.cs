using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreMessageCorrelationGrainStorageTests : PersistenceTestBase
{
    private const string StateName = "state";

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteAndRead_RoundTrip_ReturnsStoredState(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("paymentReceived/order-123");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "waitPayment", Guid.NewGuid(), "order-123"));

        await storage.WriteStateAsync(StateName, grainId, state);
        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.Subscription);
        Assert.AreEqual("order-123", readState.State.Subscription.CorrelationKey);
        Assert.AreEqual("waitPayment", readState.State.Subscription.ActivityId);
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteAndRead_RoundTrip_PreservesSubscriptionDetails(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var instanceId = Guid.NewGuid();
        var hostActivityInstanceId = Guid.NewGuid();
        var grainId = NewGrainId("orderCancelled/corr-key-1");
        var state = CreateGrainState(
            new MessageSubscription(instanceId, "activity-1", hostActivityInstanceId, "corr-key-1"));

        await storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);

        var sub = readState.State.Subscription!;
        Assert.AreEqual(instanceId, sub.WorkflowInstanceId);
        Assert.AreEqual("activity-1", sub.ActivityId);
        Assert.AreEqual(hostActivityInstanceId, sub.HostActivityInstanceId);
        Assert.AreEqual("orderCancelled/corr-key-1", sub.MessageName);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_WithCorrectETag_Succeeds(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("msg1/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await storage.WriteStateAsync(StateName, grainId, state);
        var firstETag = state.ETag;

        state.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act2", Guid.NewGuid(), "key1");
        await storage.WriteStateAsync(StateName, grainId, state);

        Assert.AreNotEqual(firstETag, state.ETag);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);
        Assert.AreEqual("act2", readState.State.Subscription!.ActivityId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("staleMsg/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act2", Guid.NewGuid(), "key1");
        await storage.WriteStateAsync(StateName, grainId, concurrentState);

        state.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act3", Guid.NewGuid(), "key1");
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => storage.WriteStateAsync(StateName, grainId, state));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_RemoveSubscription_Succeeds(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("diffRemove/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await storage.WriteStateAsync(StateName, grainId, state);

        state.State.Subscription = null;
        await storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsNull(readState.State.Subscription);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Clear_RemovesState_SubsequentReadReturnsDefault(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("clearMsg/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await storage.WriteStateAsync(StateName, grainId, state);

        await storage.ClearStateAsync(StateName, grainId, state);
        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsNull(readState.ETag);
        Assert.IsFalse(readState.RecordExists);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Clear_WithStaleETag_ThrowsInconsistentStateException(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("clearStale/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act2", Guid.NewGuid(), "key1");
        await storage.WriteStateAsync(StateName, grainId, concurrentState);

        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => storage.ClearStateAsync(StateName, grainId, state));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Clear_NonExistentGrain_IsNoOp(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("noop/key1");
        var state = CreateEmptyGrainState();

        await storage.ClearStateAsync(StateName, grainId, state);

        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Read_NonExistentKey_LeavesStateUnchanged(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("missing/key1");
        var state = CreateEmptyGrainState();

        await storage.ReadStateAsync(StateName, grainId, state);

        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task DifferentGrainIds_AreIsolated(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId1 = NewGrainId("msgA/keyA");
        var grainId2 = NewGrainId("msgB/keyB");

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var state1 = CreateGrainState(
            new MessageSubscription(id1, "actA", Guid.NewGuid(), "keyA"));
        var state2 = CreateGrainState(
            new MessageSubscription(id2, "actB", Guid.NewGuid(), "keyB"));

        await storage.WriteStateAsync(StateName, grainId1, state1);
        await storage.WriteStateAsync(StateName, grainId2, state2);

        var read1 = CreateEmptyGrainState();
        var read2 = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId1, read1);
        await storage.ReadStateAsync(StateName, grainId2, read2);

        Assert.IsNotNull(read1.State.Subscription);
        Assert.AreEqual(id1, read1.State.Subscription.WorkflowInstanceId);
        Assert.IsNotNull(read2.State.Subscription);
        Assert.AreEqual(id2, read2.State.Subscription.WorkflowInstanceId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteClearWrite_ReCreatesSameGrainId(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreMessageCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("recreate/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await storage.WriteStateAsync(StateName, grainId, state);

        await storage.ClearStateAsync(StateName, grainId, state);

        var newState = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act2", Guid.NewGuid(), "key2"));
        await storage.WriteStateAsync(StateName, grainId, newState);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.Subscription);
        Assert.AreEqual("key2", readState.State.Subscription.CorrelationKey);
        Assert.IsTrue(readState.RecordExists);
    }

    private static GrainId NewGrainId(string key)
        => GrainId.Create("messagecorrelation", key);

    private static TestGrainState<MessageCorrelationState> CreateGrainState(
        MessageSubscription subscription)
    {
        var state = new TestGrainState<MessageCorrelationState>
        {
            State = new MessageCorrelationState { Subscription = subscription }
        };
        return state;
    }

    private static TestGrainState<MessageCorrelationState> CreateEmptyGrainState()
        => new()
        {
            State = new MessageCorrelationState()
        };
}
