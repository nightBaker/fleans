using System.Data.Common;
using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreSignalCorrelationGrainStorageTests : PersistenceTestBase
{
    private const string StateName = "state";

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteAndRead_RoundTrip_ReturnsStoredState(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("approvalSignal");
        var instanceId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var sub1 = new SignalSubscription(instanceId, "waitApproval", hostId);
        var sub2 = new SignalSubscription(Guid.NewGuid(), "waitConfirm", Guid.NewGuid());
        var state = CreateGrainState(sub1, sub2);

        await WriteAsync(storage, grainId, state);
        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(2, readState.State.Subscriptions.Count);
        Assert.IsTrue(readState.State.Subscriptions.Any(s => s.ActivityId == "waitConfirm"));
        var approval = readState.State.Subscriptions.Single(s => s.ActivityId == "waitApproval");
        Assert.AreEqual(instanceId, approval.WorkflowInstanceId);
        Assert.AreEqual(hostId, approval.HostActivityInstanceId);
        Assert.AreEqual("approvalSignal", approval.SignalName);
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_WithCorrectETag_Succeeds(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("sig1");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);
        var firstETag = state.ETag;

        state.State.AddSubscription(new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);

        Assert.AreNotEqual(firstETag, state.ETag);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);
        Assert.AreEqual(2, readState.State.Subscriptions.Count);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("staleSig");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.AddSubscription(new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        await WriteAsync(storage, grainId, concurrentState);

        state.State.AddSubscription(new SignalSubscription(Guid.NewGuid(), "act3", Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => storage.WriteStateAsync(StateName, grainId, state));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_DiffRemovesSubscription(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("diffRemove");
        var sub1 = new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid());
        var sub2 = new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid());
        var state = CreateGrainState(sub1, sub2);
        await WriteAsync(storage, grainId, state);

        state.State.RemoveSubscription(sub1.WorkflowInstanceId, sub1.ActivityId);
        await WriteAsync(storage, grainId, state);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);
        Assert.AreEqual(1, readState.State.Subscriptions.Count);
        Assert.AreEqual("act2", readState.State.Subscriptions[0].ActivityId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Clear_RemovesState_SubsequentReadReturnsDefault(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("clearSig");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);

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
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("clearStale");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.AddSubscription(new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        await WriteAsync(storage, grainId, concurrentState);

        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => storage.ClearStateAsync(StateName, grainId, state));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Clear_NonExistentGrain_IsNoOp(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("noop");
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
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("missing");
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
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId1 = NewGrainId("sigA");
        var grainId2 = NewGrainId("sigB");

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var state1 = CreateGrainState(
            new SignalSubscription(id1, "actA", Guid.NewGuid()));
        var state2 = CreateGrainState(
            new SignalSubscription(id2, "actB", Guid.NewGuid()));

        await WriteAsync(storage, grainId1, state1);
        await WriteAsync(storage, grainId2, state2);

        var read1 = CreateEmptyGrainState();
        var read2 = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId1, read1);
        await storage.ReadStateAsync(StateName, grainId2, read2);

        Assert.AreEqual(1, read1.State.Subscriptions.Count);
        Assert.AreEqual(id1, read1.State.Subscriptions[0].WorkflowInstanceId);
        Assert.AreEqual(1, read2.State.Subscriptions.Count);
        Assert.AreEqual(id2, read2.State.Subscriptions[0].WorkflowInstanceId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteClearWrite_ReCreatesSameGrainId(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId("recreate");
        var state = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);

        await storage.ClearStateAsync(StateName, grainId, state);

        var newState = CreateGrainState(
            new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        await WriteAsync(storage, grainId, newState);

        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(1, readState.State.Subscriptions.Count);
        Assert.AreEqual("act2", readState.State.Subscriptions[0].ActivityId);
        Assert.IsTrue(readState.RecordExists);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task BucketsOfOneSignal_AreIsolated_AndClearOnlyTheirOwnRows(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);
        var bucket0 = NewGrainId("sharedSignal", 0);
        var bucket1 = NewGrainId("sharedSignal", 1);
        var state0 = CreateGrainState(SeedSubscriptions(3));
        var state1 = CreateGrainState(SeedSubscriptions(2));
        await WriteAsync(storage, bucket0, state0);
        await WriteAsync(storage, bucket1, state1);

        // Act — a broadcast clears bucket 0 only
        state0.State.TakeAllSubscriptions();
        await WriteAsync(storage, bucket0, state0);

        // Assert
        Assert.AreEqual(0, (await ReadAsync(storage, bucket0)).State.Subscriptions.Count);
        var read1 = await ReadAsync(storage, bucket1);
        Assert.AreEqual(2, read1.State.Subscriptions.Count);
        Assert.IsTrue(read1.State.Subscriptions.All(s => s.SignalName == "sharedSignal" && s.Bucket == 1));
        Assert.AreEqual("sharedSignal", read1.State.SignalName);
        Assert.AreEqual(1, read1.State.Bucket);
    }

    // --- #859: a write touches only the changed rows, never all N subscriptions ---

    private const int SeededSubscriptions = 200;

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Subscribe_WithManyExisting_DoesNotReadSubscriptionRows(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (storage, sql) = CreateRecordingStorage(fixture);
        var grainId = NewGrainId("bigSignal");
        var state = CreateGrainState(SeedSubscriptions());
        await WriteAsync(storage, grainId, state);

        sql.Clear();
        state.State.AddSubscription(new SignalSubscription(Guid.NewGuid(), "late", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);

        Assert.AreEqual(0, sql.Count(c => IsSelect(c) && TouchesSubscriptions(c)),
            "subscribe must not load existing subscription rows:\n" + string.Join("\n", sql));
        Assert.AreEqual(1, sql.Count(c => IsInsert(c) && TouchesSubscriptions(c)));

        var readState = await ReadAsync(storage, grainId);
        Assert.AreEqual(SeededSubscriptions + 1, readState.State.Subscriptions.Count);
        Assert.IsTrue(readState.State.Subscriptions.Any(s => s.ActivityId == "late"));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Unsubscribe_WithManyExisting_DeletesOnlyThatRow(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (storage, sql) = CreateRecordingStorage(fixture);
        var grainId = NewGrainId("bigSignalUnsub");
        var seeded = SeedSubscriptions();
        var state = CreateGrainState(seeded);
        await WriteAsync(storage, grainId, state);

        sql.Clear();
        var victim = seeded[42];
        Assert.IsTrue(state.State.RemoveSubscription(victim.WorkflowInstanceId, victim.ActivityId));
        await WriteAsync(storage, grainId, state);

        Assert.AreEqual(0, sql.Count(c => IsSelect(c) && TouchesSubscriptions(c)),
            "unsubscribe must not load existing subscription rows:\n" + string.Join("\n", sql));
        Assert.AreEqual(1, sql.Count(c => IsDelete(c) && TouchesSubscriptions(c)));

        var readState = await ReadAsync(storage, grainId);
        Assert.AreEqual(SeededSubscriptions - 1, readState.State.Subscriptions.Count);
        Assert.IsFalse(readState.State.Subscriptions.Any(s =>
            s.WorkflowInstanceId == victim.WorkflowInstanceId && s.ActivityId == victim.ActivityId));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task TakeAll_WithManyExisting_ClearsInOneSetBasedDelete(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (storage, sql) = CreateRecordingStorage(fixture);
        var grainId = NewGrainId("bigSignalBroadcast");
        var state = CreateGrainState(SeedSubscriptions());
        await WriteAsync(storage, grainId, state);

        sql.Clear();
        var taken = state.State.TakeAllSubscriptions();
        await WriteAsync(storage, grainId, state);

        Assert.AreEqual(SeededSubscriptions, taken.Count);
        Assert.AreEqual(0, sql.Count(c => IsSelect(c) && TouchesSubscriptions(c)));
        Assert.AreEqual(1, sql.Count(c => IsDelete(c) && TouchesSubscriptions(c)),
            "broadcast clear must be one set-based delete:\n" + string.Join("\n", sql));

        var readState = await ReadAsync(storage, grainId);
        Assert.AreEqual(0, readState.State.Subscriptions.Count);
        Assert.IsTrue(readState.RecordExists);
        Assert.AreEqual(state.ETag, readState.ETag);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task TakeAllThenSubscribe_InOneChangeSet_KeepsOnlyNewSubscription(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);
        var grainId = NewGrainId("clearThenAdd");
        var state = CreateGrainState(SeedSubscriptions(3));
        await WriteAsync(storage, grainId, state);

        state.State.TakeAllSubscriptions();
        state.State.AddSubscription(new SignalSubscription(Guid.NewGuid(), "next-round", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);

        var readState = await ReadAsync(storage, grainId);
        Assert.AreEqual(1, readState.State.Subscriptions.Count);
        Assert.AreEqual("next-round", readState.State.Subscriptions[0].ActivityId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task RemoveThenReAddSameKey_InOneChangeSet_ReplacesRow(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);
        var grainId = NewGrainId("reAdd");
        var instanceId = Guid.NewGuid();
        var state = CreateGrainState(new SignalSubscription(instanceId, "act1", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);

        var newHostId = Guid.NewGuid();
        state.State.RemoveSubscription(instanceId, "act1");
        state.State.AddSubscription(new SignalSubscription(instanceId, "act1", newHostId));
        await WriteAsync(storage, grainId, state);

        var readState = await ReadAsync(storage, grainId);
        Assert.AreEqual(1, readState.State.Subscriptions.Count);
        Assert.AreEqual(newHostId, readState.State.Subscriptions[0].HostActivityInstanceId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task FailedWrite_KeepsChangeSet_ForNextWrite(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);
        var grainId = NewGrainId("retryAfterFailure");
        var state = CreateGrainState(new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await WriteAsync(storage, grainId, state);
        var goodETag = state.ETag;

        state.State.AddSubscription(new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        state.ETag = "stale";
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => storage.WriteStateAsync(StateName, grainId, state));

        // Nothing from the failed write landed; the retained change set lands on the retry.
        Assert.AreEqual(1, (await ReadAsync(storage, grainId)).State.Subscriptions.Count);
        state.ETag = goodETag;
        await WriteAsync(storage, grainId, state);

        var readState = await ReadAsync(storage, grainId);
        Assert.AreEqual(2, readState.State.Subscriptions.Count);
    }

    private static SignalSubscription[] SeedSubscriptions(int count = SeededSubscriptions)
        => Enumerable.Range(0, count)
            .Select(i => new SignalSubscription(Guid.NewGuid(), $"act{i}", Guid.NewGuid()))
            .ToArray();

    private static async Task<TestGrainState<SignalCorrelationState>> ReadAsync(
        EfCoreSignalCorrelationGrainStorage storage, GrainId grainId)
    {
        var readState = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, readState);
        return readState;
    }

    private static (EfCoreSignalCorrelationGrainStorage Storage, SqlRecorder Sql) CreateRecordingStorage(
        IPersistenceTestFixture fixture)
    {
        var recorder = new SqlRecorder();
        var options = new DbContextOptionsBuilder<FleanCommandDbContext>(
                ((TestDbContextFactory)fixture.CommandFactory).Options)
            .AddInterceptors(recorder)
            .Options;
        return (new EfCoreSignalCorrelationGrainStorage(new TestDbContextFactory(options)), recorder);
    }

    private static bool TouchesSubscriptions(string sql) => sql.Contains("SignalSubscriptions");
    private static bool IsSelect(string sql) => sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
    private static bool IsInsert(string sql) => sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);
    private static bool IsDelete(string sql) => sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Records every SQL statement EF sends, one entry per statement of a batch.</summary>
    private sealed class SqlRecorder : DbCommandInterceptor, IEnumerable<string>
    {
        private readonly List<string> _statements = new();

        public void Clear()
        {
            lock (_statements)
                _statements.Clear();
        }

        private void Record(DbCommand command)
        {
            lock (_statements)
                _statements.AddRange(command.CommandText
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public IEnumerator<string> GetEnumerator()
        {
            lock (_statements)
                return _statements.ToList().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Mirrors SignalCorrelationGrain: the change set is accepted only after a successful write.
    private static async Task WriteAsync(
        EfCoreSignalCorrelationGrainStorage storage,
        GrainId grainId,
        TestGrainState<SignalCorrelationState> state)
    {
        await storage.WriteStateAsync(StateName, grainId, state);
        state.State.AcceptChanges();
    }

    // The grain's compound key: integer part = bucket, key extension = signal name.
    private static GrainId NewGrainId(string signalName, long bucket = 7)
        => GrainId.Create(GrainType.Create("signalcorrelation"), GrainIdKeyExtensions.CreateIntegerKey(bucket, signalName));

    private static TestGrainState<SignalCorrelationState> CreateGrainState(
        params SignalSubscription[] subscriptions)
    {
        var state = new TestGrainState<SignalCorrelationState> { State = new SignalCorrelationState() };
        foreach (var sub in subscriptions)
            state.State.AddSubscription(sub);
        return state;
    }

    private static TestGrainState<SignalCorrelationState> CreateEmptyGrainState()
        => new()
        {
            State = new SignalCorrelationState()
        };
}
