using System.Dynamic;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Persistence.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EfCoreEventStoreTests
{
    private SqliteConnection _connection = null!;
    private TestDbContextFactory _dbContextFactory = null!;
    private EfCoreEventStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);
        var projection = new EfCoreWorkflowStateProjection(_dbContextFactory);
        _store = new EfCoreEventStore(_dbContextFactory, projection);

        using var db = _dbContextFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
    }

    // --- Event Append + Read ---

    [TestMethod]
    public async Task AppendAndReadEvents_RoundTrip()
    {
        var grainId = "grain/test-1";
        var events = new List<IDomainEvent>
        {
            new WorkflowStarted(Guid.NewGuid(), "process:1", Guid.NewGuid()),
            new ExecutionStarted(),
            new ActivitySpawned(Guid.NewGuid(), "start", "StartEvent", Guid.NewGuid(), null, null, null)
        };

        var result = await _store.AppendEventsAsync(grainId, events, startVersion: 1);
        Assert.IsTrue(result);

        var loaded = await _store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(3, loaded.Count);
        Assert.IsInstanceOfType<WorkflowStarted>(loaded[0]);
        Assert.IsInstanceOfType<ExecutionStarted>(loaded[1]);
        Assert.IsInstanceOfType<ActivitySpawned>(loaded[2]);
    }

    [TestMethod]
    public async Task ReadEvents_AfterVersion_ReturnsOnlyLater()
    {
        var grainId = "grain/test-2";
        var events = new List<IDomainEvent>
        {
            new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()),
            new ExecutionStarted(),
            new WorkflowCompleted()
        };

        await _store.AppendEventsAsync(grainId, events, startVersion: 1);

        var loaded = await _store.ReadEventsAsync(grainId, afterVersion: 2);
        Assert.AreEqual(1, loaded.Count);
        Assert.IsInstanceOfType<WorkflowCompleted>(loaded[0]);
    }

    [TestMethod]
    public async Task ReadEvents_EmptyGrain_ReturnsEmptyList()
    {
        var loaded = await _store.ReadEventsAsync("grain/nonexistent", afterVersion: 0);
        Assert.AreEqual(0, loaded.Count);
    }

    [TestMethod]
    public async Task AppendEvents_EmptyList_ReturnsTrue()
    {
        var result = await _store.AppendEventsAsync("grain/x", [], startVersion: 1);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task AppendEvents_VersionConflict_ReturnsFalse()
    {
        var grainId = "grain/conflict";
        var events1 = new List<IDomainEvent> { new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()) };
        var events2 = new List<IDomainEvent> { new ExecutionStarted() };

        var result1 = await _store.AppendEventsAsync(grainId, events1, startVersion: 1);
        Assert.IsTrue(result1);

        // Same version — should conflict
        var result2 = await _store.AppendEventsAsync(grainId, events2, startVersion: 1);
        Assert.IsFalse(result2);
    }

    [TestMethod]
    public async Task AppendEvents_PreservesOrdering()
    {
        var grainId = "grain/ordering";
        var ids = Enumerable.Range(0, 10)
            .Select(_ => Guid.NewGuid())
            .ToList();

        var events = ids.Select(id =>
            (IDomainEvent)new ActivityExecutionStarted(id)).ToList();

        await _store.AppendEventsAsync(grainId, events, startVersion: 1);

        var loaded = await _store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(10, loaded.Count);

        for (int i = 0; i < 10; i++)
        {
            var evt = (ActivityExecutionStarted)loaded[i];
            Assert.AreEqual(ids[i], evt.ActivityInstanceId);
        }
    }

    // --- ExpandoObject Round-trip ---

    [TestMethod]
    public async Task AppendAndRead_ActivityCompleted_PreservesExpandoObject()
    {
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

        await _store.AppendEventsAsync(grainId, events, startVersion: 1);
        var loaded = await _store.ReadEventsAsync(grainId, afterVersion: 0);

        var completed = (ActivityCompleted)loaded[0];
        var loadedDict = (IDictionary<string, object?>)completed.Variables;

        Assert.AreEqual(42L, loadedDict["count"]);
        Assert.AreEqual("test-value", loadedDict["name"]);
        Assert.AreEqual(true, loadedDict["active"]);
    }

    // --- Snapshot ---

    [TestMethod]
    public async Task ReadSnapshot_EmptyGrain_ReturnsNullAndZero()
    {
        var grainId = Guid.NewGuid().ToString();
        var (state, version) = await _store.ReadSnapshotAsync(grainId);
        Assert.IsNull(state);
        Assert.AreEqual(0, version);
    }

    [TestMethod]
    public async Task WriteAndReadSnapshot_RoundTrip()
    {
        var id = Guid.NewGuid();
        var grainId = id.ToString();
        var state = CreateTestState(id);

        await _store.WriteSnapshotAsync(grainId, version: 5, state);

        var (loaded, version) = await _store.ReadSnapshotAsync(grainId);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(5, version);
        Assert.AreEqual(state.Id, loaded.Id);
        Assert.AreEqual(state.IsStarted, loaded.IsStarted);
        Assert.AreEqual(state.ProcessDefinitionId, loaded.ProcessDefinitionId);

        // Verify state is stored in normalized WorkflowInstances table, not as JSON blob
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var row = await db.WorkflowInstances.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        Assert.IsNotNull(row, "Snapshot state should exist in WorkflowInstances table");

        // Verify no JSON payload in snapshot metadata table
        var snapshotMeta = await db.WorkflowSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.GrainId == grainId);
        Assert.IsNotNull(snapshotMeta);
        Assert.AreEqual(5, snapshotMeta.Version);
    }

    [TestMethod]
    public async Task WriteSnapshot_UpdateExisting()
    {
        var id = Guid.NewGuid();
        var grainId = id.ToString();
        var state1 = CreateTestState(id);

        await _store.WriteSnapshotAsync(grainId, version: 5, state1);

        // Update the same grain's snapshot with new version
        var state2 = CreateTestState(id);

        await _store.WriteSnapshotAsync(grainId, version: 10, state2);

        var (loaded, version) = await _store.ReadSnapshotAsync(grainId);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(10, version);
        Assert.AreEqual(id, loaded.Id);
    }

    // --- Isolation ---

    [TestMethod]
    public async Task DifferentGrainIds_AreIsolated()
    {
        var events1 = new List<IDomainEvent> { new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()) };
        var events2 = new List<IDomainEvent> { new ExecutionStarted() };

        await _store.AppendEventsAsync("grain/a", events1, startVersion: 1);
        await _store.AppendEventsAsync("grain/b", events2, startVersion: 1);

        var loadedA = await _store.ReadEventsAsync("grain/a", afterVersion: 0);
        var loadedB = await _store.ReadEventsAsync("grain/b", afterVersion: 0);

        Assert.AreEqual(1, loadedA.Count);
        Assert.AreEqual(1, loadedB.Count);
        Assert.IsInstanceOfType<WorkflowStarted>(loadedA[0]);
        Assert.IsInstanceOfType<ExecutionStarted>(loadedB[0]);
    }

    // --- Multi-batch Append ---

    [TestMethod]
    public async Task AppendEvents_MultipleBatches_VersionsContinue()
    {
        var grainId = "grain/multi-batch";

        var batch1 = new List<IDomainEvent> { new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()) };
        var batch2 = new List<IDomainEvent> { new ExecutionStarted(), new WorkflowCompleted() };

        await _store.AppendEventsAsync(grainId, batch1, startVersion: 1);
        await _store.AppendEventsAsync(grainId, batch2, startVersion: 2);

        var all = await _store.ReadEventsAsync(grainId, afterVersion: 0);
        Assert.AreEqual(3, all.Count);
        Assert.IsInstanceOfType<WorkflowStarted>(all[0]);
        Assert.IsInstanceOfType<ExecutionStarted>(all[1]);
        Assert.IsInstanceOfType<WorkflowCompleted>(all[2]);
    }

    private static WorkflowInstanceState CreateTestState(Guid? id = null)
    {
        var state = new WorkflowInstanceState();
        // Use null ProcessDefinitionId to avoid FK constraint against ProcessDefinitions table
        state.Initialize(id ?? Guid.NewGuid(), null, Guid.NewGuid());
        state.Start();
        return state;
    }
}
