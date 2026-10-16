using System.Dynamic;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Persistence.Events;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreEventStoreTests : PersistenceTestBase
{
    private static readonly Serializer Serializer =
        new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<Serializer>();

    private static EfCoreEventStore CreateStore(IPersistenceTestFixture fixture, int? maxEventsPerLoad = null)
    {
        var opts = Options.Create(new FleansPersistenceOptions { MaxEventsPerLoad = maxEventsPerLoad ?? 1000 });
        return new EfCoreEventStore(fixture.CommandFactory, Serializer, opts);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task AppendAndReadEvents_RoundTrip(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var grainId = "grain/test-1";
        var events = new List<WorkflowEvent>
        {
            new WorkflowStarted(Guid.NewGuid(), "process:1", Guid.NewGuid()),
            new ExecutionStarted(),
            new ActivitySpawned(Guid.NewGuid(), "start", "StartEvent", Guid.NewGuid(), null, null, null)
        };

        var result = await store.AppendEventsAsync(grainId, events, expectedVersion: 0);
        Assert.IsTrue(result);

        var loaded = await store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(3, loaded.Count);
        Assert.IsInstanceOfType<WorkflowStarted>(loaded[0]);
        Assert.IsInstanceOfType<ExecutionStarted>(loaded[1]);
        Assert.IsInstanceOfType<ActivitySpawned>(loaded[2]);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadEvents_AfterVersion_ReturnsOnlyLater(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var grainId = "grain/test-2";
        var events = new List<WorkflowEvent>
        {
            new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()),
            new ExecutionStarted(),
            new WorkflowCompleted()
        };

        await store.AppendEventsAsync(grainId, events, expectedVersion: 0);

        var loaded = await store.ReadEventsAsync(grainId, afterVersion: 2);
        Assert.AreEqual(1, loaded.Count);
        Assert.IsInstanceOfType<WorkflowCompleted>(loaded[0]);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadEvents_EmptyGrain_ReturnsEmptyList(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var loaded = await store.ReadEventsAsync("grain/nonexistent", afterVersion: 0);
        Assert.AreEqual(0, loaded.Count);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task AppendEvents_EmptyList_ReturnsTrue(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var result = await store.AppendEventsAsync("grain/x", [], expectedVersion: 0);
        Assert.IsTrue(result);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task AppendEvents_VersionConflict_ReturnsFalse(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var grainId = "grain/conflict";
        var events1 = new List<WorkflowEvent> { new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()) };
        var events2 = new List<WorkflowEvent> { new ExecutionStarted() };

        var result1 = await store.AppendEventsAsync(grainId, events1, expectedVersion: 0);
        Assert.IsTrue(result1);

        var result2 = await store.AppendEventsAsync(grainId, events2, expectedVersion: 0);
        Assert.IsFalse(result2);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task AppendEvents_PreservesOrdering(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var grainId = "grain/ordering";
        var ids = Enumerable.Range(0, 10)
            .Select(_ => Guid.NewGuid())
            .ToList();

        var events = ids.Select(id =>
            (WorkflowEvent)new ActivityExecutionStarted(id)).ToList();

        await store.AppendEventsAsync(grainId, events, expectedVersion: 0);

        var loaded = await store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(10, loaded.Count);

        for (int i = 0; i < 10; i++)
        {
            var evt = (ActivityExecutionStarted)loaded[i];
            Assert.AreEqual(ids[i], evt.ActivityInstanceId);
        }
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task AppendAndRead_ActivityCompleted_PreservesExpandoObject(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var grainId = "grain/expando";
        var variables = new ExpandoObject();
        var dict = (IDictionary<string, object?>)variables;
        dict["count"] = 42L;
        dict["name"] = "test-value";
        dict["active"] = true;

        var events = new List<WorkflowEvent>
        {
            new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), variables)
        };

        await store.AppendEventsAsync(grainId, events, expectedVersion: 0);
        var loaded = await store.ReadEventsAsync(grainId, afterVersion: 0);

        var completed = (ActivityCompleted)loaded[0];
        var loadedDict = (IDictionary<string, object?>)completed.Variables;

        Assert.AreEqual(42L, loadedDict["count"]);
        Assert.AreEqual("test-value", loadedDict["name"]);
        Assert.AreEqual(true, loadedDict["active"]);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadSnapshot_EmptyGrain_ReturnsNullAndZero(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var grainId = Guid.NewGuid().ToString();
        var (state, version) = await store.ReadSnapshotAsync(grainId);
        Assert.IsNull(state);
        Assert.AreEqual(0, version);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteAndReadSnapshot_RoundTrip_KeepsTheFullState(PersistenceProvider provider)
    {
        // The snapshot is the grain state itself, including the members the read model has no
        // columns for — activation restores them from here, not from the projection.
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var id = Guid.NewGuid();
        var grainId = id.ToString();
        var state = CreateTestState(id);
        var userTaskId = Guid.NewGuid();
        var userTask = new UserTaskMetadata();
        userTask.Initialize("alice", ["managers"], ["bob"], ["approved"]);
        userTask.Claim("alice", DateTimeOffset.UtcNow);
        state.UserTasks[userTaskId] = userTask;
        var txId = Guid.NewGuid();
        state.TransactionOutcomes[txId] = new TransactionOutcomeRecord(TransactionOutcome.Completed, null, null);
        state.ScopeHostExecutionScopes[Guid.NewGuid()] = Guid.NewGuid();
        state.AddPendingOperation("op-1", new PendingSignalDeliveryPayload("signal1", Guid.NewGuid()));
        state.RecordAppliedOperation("op-0", null);
        state.AddConditionalWatcher(Guid.NewGuid(), "cond1", "x > 1", state.GetRootVariablesId());
        state.AddCompensationSnapshot(new CompletedActivitySnapshot(
            Guid.NewGuid(), "task1", new ExpandoObject(), state.NextCompensationSequence, null));

        await store.WriteSnapshotAsync(grainId, version: 5, state);

        var (loaded, version) = await store.ReadSnapshotAsync(grainId);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(5, version);
        Assert.AreEqual(state.Id, loaded.Id);
        Assert.AreEqual(state.IsStarted, loaded.IsStarted);
        Assert.AreEqual(state.CreatedAt, loaded.CreatedAt);
        Assert.AreEqual("alice", loaded.UserTasks[userTaskId].ClaimedBy);
        Assert.AreEqual(TransactionOutcome.Completed, loaded.TransactionOutcomes[txId].Outcome);
        Assert.HasCount(1, loaded.ScopeHostExecutionScopes);
        Assert.IsTrue(loaded.PendingOperations.ContainsKey("op-1"));
        Assert.IsTrue(loaded.AppliedOperations.ContainsKey("op-0"));
        Assert.HasCount(1, loaded.ConditionalWatchers);
        Assert.HasCount(1, loaded.CompensationLog);

        // Snapshots and the read model are separate: writing one does not touch the other.
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        Assert.IsFalse(await db.WorkflowInstances.AnyAsync(s => s.Id == id));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteAndReadSnapshot_RoundTrip_KeepsVariableValues(PersistenceProvider provider)
    {
        // Variables reach the snapshot through the production ExpandoObject codec
        // (ExpandoObjectSurrogateConverter) — the value shapes the API and scripts produce.
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);
        var id = Guid.NewGuid();
        var state = CreateTestState(id);
        dynamic nested = new ExpandoObject();
        nested.level = 2L;
        dynamic vars = new ExpandoObject();
        vars.count = 42L;
        vars.price = 9.5;
        vars.approved = true;
        vars.name = "order-1";
        vars.missing = null;
        vars.nested = nested;
        vars.items = new List<object?> { 1L, "two", 3.0 };
        state.MergeState(state.GetRootVariablesId(), (ExpandoObject)vars);

        await store.WriteSnapshotAsync(id.ToString(), version: 3, state);
        var (loaded, _) = await store.ReadSnapshotAsync(id.ToString());

        Assert.IsNotNull(loaded);
        var rootId = loaded.GetRootVariablesId();
        Assert.AreEqual(42L, loaded.GetVariable(rootId, "count"));
        Assert.AreEqual(9.5, loaded.GetVariable(rootId, "price"));
        Assert.AreEqual(true, loaded.GetVariable(rootId, "approved"));
        Assert.AreEqual("order-1", loaded.GetVariable(rootId, "name"));
        Assert.IsNull(loaded.GetVariable(rootId, "missing"));
        var loadedNested = (IDictionary<string, object?>)loaded.GetVariable(rootId, "nested")!;
        Assert.AreEqual(2L, loadedNested["level"]);
        CollectionAssert.AreEqual(new List<object?> { 1L, "two", 3.0 },
            ((IEnumerable<object?>)loaded.GetVariable(rootId, "items")!).ToList());
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadSnapshot_UnreadablePayload_Throws(PersistenceProvider provider)
    {
        // The grain treats a throwing read as "no snapshot" and replays the whole journal.
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);
        var grainId = Guid.NewGuid().ToString();
        await using (var db = await fixture.CommandFactory.CreateDbContextAsync())
        {
            db.WorkflowSnapshots.Add(new WorkflowSnapshotEntity
            {
                GrainId = grainId, Version = 3, Payload = [1, 2, 3], Timestamp = DateTimeOffset.UtcNow
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
    public async Task WriteSnapshot_UpdateExisting(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var id = Guid.NewGuid();
        var grainId = id.ToString();
        var state1 = CreateTestState(id);

        await store.WriteSnapshotAsync(grainId, version: 5, state1);

        var state2 = CreateTestState(id);

        await store.WriteSnapshotAsync(grainId, version: 10, state2);

        var (loaded, version) = await store.ReadSnapshotAsync(grainId);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(10, version);
        Assert.AreEqual(id, loaded.Id);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task DifferentGrainIds_AreIsolated(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var events1 = new List<WorkflowEvent> { new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()) };
        var events2 = new List<WorkflowEvent> { new ExecutionStarted() };

        await store.AppendEventsAsync("grain/a", events1, expectedVersion: 0);
        await store.AppendEventsAsync("grain/b", events2, expectedVersion: 0);

        var loadedA = await store.ReadEventsAsync("grain/a", afterVersion: 0);
        var loadedB = await store.ReadEventsAsync("grain/b", afterVersion: 0);

        Assert.AreEqual(1, loadedA.Count);
        Assert.AreEqual(1, loadedB.Count);
        Assert.IsInstanceOfType<WorkflowStarted>(loadedA[0]);
        Assert.IsInstanceOfType<ExecutionStarted>(loadedB[0]);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task AppendEvents_MultipleBatches_VersionsContinue(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var grainId = "grain/multi-batch";

        var batch1 = new List<WorkflowEvent> { new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()) };
        var batch2 = new List<WorkflowEvent> { new ExecutionStarted(), new WorkflowCompleted() };

        await store.AppendEventsAsync(grainId, batch1, expectedVersion: 0);
        await store.AppendEventsAsync(grainId, batch2, expectedVersion: 1);

        var all = await store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(3, all.Count);
        Assert.IsInstanceOfType<WorkflowStarted>(all[0]);
        Assert.IsInstanceOfType<ExecutionStarted>(all[1]);
        Assert.IsInstanceOfType<WorkflowCompleted>(all[2]);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadEventsAsync_AtCap_ReturnsAllEvents(PersistenceProvider provider)
    {
        const int cap = 5;
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture, maxEventsPerLoad: cap);

        var grainId = "grain/cap-exact";
        var events = Enumerable.Range(0, cap).Select(_ => (WorkflowEvent)new ExecutionStarted()).ToList();
        await store.AppendEventsAsync(grainId, events, expectedVersion: 0);

        var loaded = await store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(cap, loaded.Count);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadEventsAsync_OneOverCap_ThrowsInvalidOperation(PersistenceProvider provider)
    {
        const int cap = 5;
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture, maxEventsPerLoad: cap);

        var grainId = "grain/cap-over";
        var events = Enumerable.Range(0, cap + 1).Select(_ => (WorkflowEvent)new ExecutionStarted()).ToList();
        await store.AppendEventsAsync(grainId, events, expectedVersion: 0);

        InvalidOperationException? caught = null;
        try { await store.ReadEventsAsync(grainId, afterVersion: 0); }
        catch (InvalidOperationException ex) { caught = ex; }
        Assert.IsNotNull(caught, "Expected InvalidOperationException was not thrown");
        StringAssert.Contains(caught.Message, grainId);
        StringAssert.Contains(caught.Message, "afterVersion=0");
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadEventsAsync_WithAfterVersion_RespectsCap(PersistenceProvider provider)
    {
        const int cap = 5;
        const int offset = 3;
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture, maxEventsPerLoad: cap);

        // cap + offset total events; afterVersion=offset leaves exactly cap events — should not throw
        var grainId = "grain/cap-offset";
        var events = Enumerable.Range(0, cap + offset).Select(_ => (WorkflowEvent)new ExecutionStarted()).ToList();
        await store.AppendEventsAsync(grainId, events, expectedVersion: 0);

        var loaded = await store.ReadEventsAsync(grainId, afterVersion: offset);
        Assert.AreEqual(cap, loaded.Count);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadEventsAsync_UnboundedWhenCapSetHigh_NoThrow(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture, maxEventsPerLoad: int.MaxValue);

        var grainId = "grain/cap-high";
        var events = new List<WorkflowEvent>
        {
            new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()),
            new ExecutionStarted(),
            new WorkflowCompleted()
        };
        await store.AppendEventsAsync(grainId, events, expectedVersion: 0);

        var loaded = await store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(3, loaded.Count);
    }

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
    public async Task AppendEvents_StoresOccurredAtAsTimestamp(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);
        var grainId = Guid.NewGuid().ToString();
        var occurredAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await store.AppendEventsAsync(grainId, [new ExecutionStarted { OccurredAt = occurredAt }], expectedVersion: 0);

        var loaded = (ExecutionStarted)(await store.ReadEventsAsync(grainId, afterVersion: 0)).Single();
        Assert.AreEqual(occurredAt, loaded.OccurredAt);
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var row = await db.WorkflowEvents.AsNoTracking().SingleAsync(e => e.GrainId == grainId);
        Assert.AreEqual(occurredAt, row.Timestamp);
    }

    private static WorkflowInstanceState CreateTestState(Guid? id = null)
    {
        var state = new WorkflowInstanceState();
        state.Initialize(id ?? Guid.NewGuid(), null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        state.Start(DateTimeOffset.UtcNow);
        return state;
    }
}
