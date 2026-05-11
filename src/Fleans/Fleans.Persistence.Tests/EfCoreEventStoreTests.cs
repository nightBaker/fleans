using System.Dynamic;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Persistence.Events;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreEventStoreTests : PersistenceTestBase
{
    private static EfCoreEventStore CreateStore(IPersistenceTestFixture fixture, int? maxEventsPerLoad = null)
    {
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var opts = Options.Create(new FleansPersistenceOptions { MaxEventsPerLoad = maxEventsPerLoad ?? 1000 });
        return new EfCoreEventStore(fixture.CommandFactory, projection, opts);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task AppendAndReadEvents_RoundTrip(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var grainId = "grain/test-1";
        var events = new List<IDomainEvent>
        {
            new WorkflowStarted(Guid.NewGuid(), "process:1", Guid.NewGuid()),
            new ExecutionStarted(),
            new ActivitySpawned(Guid.NewGuid(), "start", "StartEvent", Guid.NewGuid(), null, null, null)
        };

        var result = await store.AppendEventsAsync(grainId, events, startVersion: 1);
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
        var events = new List<IDomainEvent>
        {
            new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()),
            new ExecutionStarted(),
            new WorkflowCompleted()
        };

        await store.AppendEventsAsync(grainId, events, startVersion: 1);

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

        var result = await store.AppendEventsAsync("grain/x", [], startVersion: 1);
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
        var events1 = new List<IDomainEvent> { new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()) };
        var events2 = new List<IDomainEvent> { new ExecutionStarted() };

        var result1 = await store.AppendEventsAsync(grainId, events1, startVersion: 1);
        Assert.IsTrue(result1);

        var result2 = await store.AppendEventsAsync(grainId, events2, startVersion: 1);
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
            (IDomainEvent)new ActivityExecutionStarted(id)).ToList();

        await store.AppendEventsAsync(grainId, events, startVersion: 1);

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

        var events = new List<IDomainEvent>
        {
            new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), variables)
        };

        await store.AppendEventsAsync(grainId, events, startVersion: 1);
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
    public async Task WriteAndReadSnapshot_RoundTrip(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var store = CreateStore(fixture);

        var id = Guid.NewGuid();
        var grainId = id.ToString();
        var state = CreateTestState(id);

        await store.WriteSnapshotAsync(grainId, version: 5, state);

        var (loaded, version) = await store.ReadSnapshotAsync(grainId);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(5, version);
        Assert.AreEqual(state.Id, loaded.Id);
        Assert.AreEqual(state.IsStarted, loaded.IsStarted);
        Assert.AreEqual(state.ProcessDefinitionId, loaded.ProcessDefinitionId);

        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var row = await db.WorkflowInstances.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        Assert.IsNotNull(row, "Snapshot state should exist in WorkflowInstances table");

        var snapshotMeta = await db.WorkflowSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.GrainId == grainId);
        Assert.IsNotNull(snapshotMeta);
        Assert.AreEqual(5, snapshotMeta.Version);
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

        var events1 = new List<IDomainEvent> { new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()) };
        var events2 = new List<IDomainEvent> { new ExecutionStarted() };

        await store.AppendEventsAsync("grain/a", events1, startVersion: 1);
        await store.AppendEventsAsync("grain/b", events2, startVersion: 1);

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

        var batch1 = new List<IDomainEvent> { new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()) };
        var batch2 = new List<IDomainEvent> { new ExecutionStarted(), new WorkflowCompleted() };

        await store.AppendEventsAsync(grainId, batch1, startVersion: 1);
        await store.AppendEventsAsync(grainId, batch2, startVersion: 2);

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
        var events = Enumerable.Range(0, cap).Select(_ => (IDomainEvent)new ExecutionStarted()).ToList();
        await store.AppendEventsAsync(grainId, events, startVersion: 1);

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
        var events = Enumerable.Range(0, cap + 1).Select(_ => (IDomainEvent)new ExecutionStarted()).ToList();
        await store.AppendEventsAsync(grainId, events, startVersion: 1);

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
        var events = Enumerable.Range(0, cap + offset).Select(_ => (IDomainEvent)new ExecutionStarted()).ToList();
        await store.AppendEventsAsync(grainId, events, startVersion: 1);

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
        var events = new List<IDomainEvent>
        {
            new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()),
            new ExecutionStarted(),
            new WorkflowCompleted()
        };
        await store.AppendEventsAsync(grainId, events, startVersion: 1);

        var loaded = await store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(3, loaded.Count);
    }

    [TestMethod]
    public void Default_MaxEventsPerLoad_Is1000()
    {
        var opts = new FleansPersistenceOptions();
        Assert.AreEqual(1000, opts.MaxEventsPerLoad);
    }

    private static WorkflowInstanceState CreateTestState(Guid? id = null)
    {
        var state = new WorkflowInstanceState();
        state.Initialize(id ?? Guid.NewGuid(), null, Guid.NewGuid());
        state.Start();
        return state;
    }
}
