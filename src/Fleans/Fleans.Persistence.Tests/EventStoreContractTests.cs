using System.Dynamic;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Persistence.Tests;

/// <summary>
/// The semantics every <see cref="IEventStore"/> provider must honour (#879), through the
/// interface only: contiguous versions, a conflict returns <c>false</c> instead of throwing, a
/// snapshot is exactly the state at its version, an idempotent retry (#877), and the
/// <c>MaxEventsPerLoad</c> guard. A provider runs it by deriving a <c>[TestClass]</c> that creates
/// its store over an empty journal; see <c>EfCoreEventStoreContractTests</c>. Tests of how a
/// provider lays its rows out belong in its own test class.
/// </summary>
public abstract class EventStoreContractTests
{
    /// <summary>
    /// The store under test over an empty journal, with the given
    /// <see cref="FleansPersistenceOptions.MaxEventsPerLoad"/>. Released by <see cref="DisposeStoreAsync"/>.
    /// </summary>
    protected abstract Task<IEventStore> CreateStoreAsync(int maxEventsPerLoad = 1000);

    /// <summary>Releases what the last <see cref="CreateStoreAsync"/> acquired.</summary>
    protected virtual ValueTask DisposeStoreAsync() => ValueTask.CompletedTask;

    [TestCleanup]
    public async Task CleanupStoreAsync() => await DisposeStoreAsync();

    // ── Versions ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AppendAndReadEvents_RoundTrip()
    {
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();

        var result = await store.AppendEventsAsync(instanceId,
            [
                new WorkflowStarted(Guid.NewGuid(), "process:1", Guid.NewGuid()),
                new ExecutionStarted(),
                new ActivitySpawned(Guid.NewGuid(), "start", "StartEvent", Guid.NewGuid(), null, null, null)
            ],
            expectedVersion: 0, batchId: Guid.NewGuid());

        Assert.IsTrue(result);
        var loaded = await store.ReadEventsAsync(instanceId, afterVersion: 0);
        Assert.HasCount(3, loaded);
        Assert.IsInstanceOfType<WorkflowStarted>(loaded[0]);
        Assert.IsInstanceOfType<ExecutionStarted>(loaded[1]);
        Assert.IsInstanceOfType<ActivitySpawned>(loaded[2]);
    }

    [TestMethod]
    public async Task ReadEvents_EmptyJournal_ReturnsEmptyList()
    {
        var store = await CreateStoreAsync();

        Assert.HasCount(0, await store.ReadEventsAsync(Guid.NewGuid(), afterVersion: 0));
    }

    [TestMethod]
    public async Task AppendEvents_EmptyList_ReturnsTrue()
    {
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();

        Assert.IsTrue(await store.AppendEventsAsync(instanceId, [], expectedVersion: 0, batchId: Guid.NewGuid()));
        Assert.HasCount(0, await store.ReadEventsAsync(instanceId, afterVersion: 0));
    }

    [TestMethod]
    public async Task AppendEvents_MultipleBatches_VersionsContinue()
    {
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();

        await store.AppendEventsAsync(instanceId,
            [new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid())], expectedVersion: 0, batchId: Guid.NewGuid());
        await store.AppendEventsAsync(instanceId,
            [new ExecutionStarted(), new WorkflowCompleted()], expectedVersion: 1, batchId: Guid.NewGuid());

        var all = await store.ReadEventsAsync(instanceId, afterVersion: 0);
        Assert.HasCount(3, all);
        Assert.IsInstanceOfType<WorkflowStarted>(all[0]);
        Assert.IsInstanceOfType<ExecutionStarted>(all[1]);
        Assert.IsInstanceOfType<WorkflowCompleted>(all[2]);
    }

    [TestMethod]
    public async Task ReadEvents_AfterVersion_ReturnsOnlyLater()
    {
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();
        await store.AppendEventsAsync(instanceId,
            [new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()), new ExecutionStarted()],
            expectedVersion: 0, batchId: Guid.NewGuid());
        await store.AppendEventsAsync(instanceId, [new WorkflowCompleted()], expectedVersion: 2, batchId: Guid.NewGuid());

        var loaded = await store.ReadEventsAsync(instanceId, afterVersion: 2);

        Assert.HasCount(1, loaded);
        Assert.IsInstanceOfType<WorkflowCompleted>(loaded[0]);
    }

    [TestMethod]
    public async Task AppendEvents_PreservesOrdering()
    {
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();

        await store.AppendEventsAsync(instanceId,
            ids.Select(id => (WorkflowEvent)new ActivityExecutionStarted(id)).ToList(),
            expectedVersion: 0, batchId: Guid.NewGuid());

        var loaded = await store.ReadEventsAsync(instanceId, afterVersion: 0);
        CollectionAssert.AreEqual(ids, loaded.Cast<ActivityExecutionStarted>().Select(e => e.ActivityInstanceId).ToList());
    }

    [TestMethod]
    public async Task ReadEvents_GapInTheJournal_Throws()
    {
        // A journal that does not continue from afterVersion (a missing batch, or afterVersion
        // inside a batch) must fail the activation rather than replay a partial history.
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();
        await store.AppendEventsAsync(instanceId, [new ExecutionStarted(), new ExecutionStarted()],
            expectedVersion: 0, batchId: Guid.NewGuid());
        await store.AppendEventsAsync(instanceId, [new WorkflowCompleted()], expectedVersion: 3, batchId: Guid.NewGuid());

        var fromStart = await CatchAsync<InvalidOperationException>(() => store.ReadEventsAsync(instanceId, afterVersion: 0));
        var midBatch = await CatchAsync<InvalidOperationException>(() => store.ReadEventsAsync(instanceId, afterVersion: 1));

        Assert.IsNotNull(fromStart);
        StringAssert.Contains(fromStart.Message, "gap");
        Assert.IsNotNull(midBatch);
    }

    [TestMethod]
    public async Task DifferentInstances_AreIsolated()
    {
        var store = await CreateStoreAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await store.AppendEventsAsync(a, [new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid())],
            expectedVersion: 0, batchId: Guid.NewGuid());
        await store.AppendEventsAsync(b, [new ExecutionStarted()], expectedVersion: 0, batchId: Guid.NewGuid());

        var loadedA = await store.ReadEventsAsync(a, afterVersion: 0);
        var loadedB = await store.ReadEventsAsync(b, afterVersion: 0);
        Assert.HasCount(1, loadedA);
        Assert.HasCount(1, loadedB);
        Assert.IsInstanceOfType<WorkflowStarted>(loadedA[0]);
        Assert.IsInstanceOfType<ExecutionStarted>(loadedB[0]);
    }

    [TestMethod]
    public async Task AppendAndRead_ActivityCompleted_PreservesExpandoObject()
    {
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();
        var variables = new ExpandoObject();
        var dict = (IDictionary<string, object?>)variables;
        dict["count"] = 42L;
        dict["name"] = "test-value";
        dict["active"] = true;

        await store.AppendEventsAsync(instanceId, [new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), variables)],
            expectedVersion: 0, batchId: Guid.NewGuid());

        var completed = (ActivityCompleted)(await store.ReadEventsAsync(instanceId, afterVersion: 0))[0];
        var loadedDict = (IDictionary<string, object?>)completed.Variables;
        Assert.AreEqual(42L, loadedDict["count"]);
        Assert.AreEqual("test-value", loadedDict["name"]);
        Assert.AreEqual(true, loadedDict["active"]);
    }

    // ── Optimistic concurrency ───────────────────────────────────────────────

    [TestMethod]
    public async Task AppendEvents_VersionConflict_ReturnsFalse()
    {
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();
        Assert.IsTrue(await store.AppendEventsAsync(instanceId,
            [new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid())], expectedVersion: 0, batchId: Guid.NewGuid()));

        var result = await store.AppendEventsAsync(instanceId, [new ExecutionStarted()], expectedVersion: 0, batchId: Guid.NewGuid());

        Assert.IsFalse(result);
        var journal = await store.ReadEventsAsync(instanceId, afterVersion: 0);
        Assert.HasCount(1, journal);
        Assert.IsInstanceOfType<WorkflowStarted>(journal[0]);
    }

    [TestMethod]
    public async Task AppendEvents_ConflictWithALongerBatch_ReturnsFalse()
    {
        // Another writer appended a multi-event batch after version 1; a writer that also saw
        // version 1 conflicts whatever its batch length.
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();
        await store.AppendEventsAsync(instanceId, [new ExecutionStarted()], expectedVersion: 0, batchId: Guid.NewGuid());
        await store.AppendEventsAsync(instanceId, [new ExecutionStarted(), new ExecutionStarted()],
            expectedVersion: 1, batchId: Guid.NewGuid());

        var result = await store.AppendEventsAsync(instanceId, [new WorkflowCompleted()], expectedVersion: 1, batchId: Guid.NewGuid());

        Assert.IsFalse(result);
        Assert.HasCount(3, await store.ReadEventsAsync(instanceId, afterVersion: 0));
    }

    // ── Idempotent retry (#877) ──────────────────────────────────────────────

    [TestMethod]
    public async Task AppendEvents_RetryOfAStoredBatch_ReturnsTrueWithoutAppending()
    {
        // The first try was stored but reported as failed (e.g. a timeout after the commit).
        // The retry carries the same batch id at the same version and must not store it again.
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        List<WorkflowEvent> batch = [new ExecutionStarted(), new WorkflowCompleted()];
        await store.AppendEventsAsync(instanceId, [new ExecutionStarted()], expectedVersion: 0, batchId: Guid.NewGuid());
        Assert.IsTrue(await store.AppendEventsAsync(instanceId, batch, expectedVersion: 1, batchId: batchId));

        var retry = await store.AppendEventsAsync(instanceId, batch, expectedVersion: 1, batchId: batchId);

        Assert.IsTrue(retry);
        Assert.HasCount(3, await store.ReadEventsAsync(instanceId, afterVersion: 0));
        // The next append continues right after the batch.
        Assert.IsTrue(await store.AppendEventsAsync(instanceId, [new WorkflowCompleted()], expectedVersion: 3, batchId: Guid.NewGuid()));
        Assert.HasCount(4, await store.ReadEventsAsync(instanceId, afterVersion: 0));
    }

    [TestMethod]
    public async Task AppendEvents_RetryWhereAnotherBatchIsStored_ReturnsFalse()
    {
        // The batch id only recognises the writer's own batch: another writer's batch at the
        // same version is still a conflict.
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();
        await store.AppendEventsAsync(instanceId, [new ExecutionStarted()], expectedVersion: 0, batchId: Guid.NewGuid());
        var batchId = Guid.NewGuid();

        var first = await store.AppendEventsAsync(instanceId, [new WorkflowCompleted()], expectedVersion: 0, batchId: batchId);
        var retry = await store.AppendEventsAsync(instanceId, [new WorkflowCompleted()], expectedVersion: 0, batchId: batchId);

        Assert.IsFalse(first);
        Assert.IsFalse(retry);
        var journal = await store.ReadEventsAsync(instanceId, afterVersion: 0);
        Assert.HasCount(1, journal);
        Assert.IsInstanceOfType<ExecutionStarted>(journal[0]);
    }

    [TestMethod]
    public async Task AppendEvents_RetryWithADifferentEventCount_Throws()
    {
        // A retry must resend exactly the stored batch; anything else is a caller bug, not success.
        var store = await CreateStoreAsync();
        var instanceId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        await store.AppendEventsAsync(instanceId, [new ExecutionStarted()], expectedVersion: 0, batchId: batchId);

        var error = await CatchAsync<InvalidOperationException>(() => store.AppendEventsAsync(instanceId,
            [new ExecutionStarted(), new WorkflowCompleted()], expectedVersion: 0, batchId: batchId));

        Assert.IsNotNull(error);
        Assert.HasCount(1, await store.ReadEventsAsync(instanceId, afterVersion: 0));
    }

    // ── Snapshots ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ReadSnapshot_NoSnapshot_ReturnsNullAndZero()
    {
        var store = await CreateStoreAsync();

        var (state, version) = await store.ReadSnapshotAsync(Guid.NewGuid());

        Assert.IsNull(state);
        Assert.AreEqual(0, version);
    }

    [TestMethod]
    public async Task WriteAndReadSnapshot_RoundTrip_KeepsTheFullState()
    {
        // The snapshot is the grain state itself, including the members the read model has no
        // columns for — activation restores them from here, not from the projection.
        var store = await CreateStoreAsync();
        var id = Guid.NewGuid();
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

        await store.WriteSnapshotAsync(id, version: 5, state);

        var (loaded, version) = await store.ReadSnapshotAsync(id);
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
    }

    [TestMethod]
    public async Task WriteAndReadSnapshot_RoundTrip_KeepsVariableValues()
    {
        // Variables reach the snapshot through the production ExpandoObject codec
        // (ExpandoObjectSurrogateConverter) — the value shapes the API and scripts produce.
        var store = await CreateStoreAsync();
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

        await store.WriteSnapshotAsync(id, version: 3, state);
        var (loaded, _) = await store.ReadSnapshotAsync(id);

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

    [TestMethod]
    public async Task WriteSnapshot_ReplacesThePreviousOne_AtItsVersion()
    {
        var store = await CreateStoreAsync();
        var id = Guid.NewGuid();
        var first = CreateTestState(id);
        await store.WriteSnapshotAsync(id, version: 5, first);
        var second = CreateTestState(id);
        second.Complete(DateTimeOffset.UtcNow);

        await store.WriteSnapshotAsync(id, version: 10, second);

        var (loaded, version) = await store.ReadSnapshotAsync(id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(10, version);
        Assert.AreEqual(id, loaded.Id);
        Assert.IsTrue(loaded.IsCompleted, "the snapshot must be the state written with its version");
    }

    [TestMethod]
    public async Task Snapshots_OfDifferentInstances_AreIsolated()
    {
        var store = await CreateStoreAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await store.WriteSnapshotAsync(a, version: 2, CreateTestState(a));
        await store.WriteSnapshotAsync(b, version: 7, CreateTestState(b));

        var (loadedA, versionA) = await store.ReadSnapshotAsync(a);
        var (loadedB, versionB) = await store.ReadSnapshotAsync(b);
        Assert.AreEqual((a, 2), (loadedA!.Id, versionA));
        Assert.AreEqual((b, 7), (loadedB!.Id, versionB));
    }

    // ── Purge (#862) ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DeleteInstance_RemovesOnlyThatInstancesJournalAndSnapshot_Idempotently()
    {
        var store = await CreateStoreAsync();
        var purged = Guid.NewGuid();
        var other = Guid.NewGuid();
        foreach (var id in new[] { purged, other })
        {
            await store.AppendEventsAsync(id, [new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid())], expectedVersion: 0, batchId: Guid.NewGuid());
            await store.AppendEventsAsync(id, [new WorkflowCompleted()], expectedVersion: 1, batchId: Guid.NewGuid());
            await store.WriteSnapshotAsync(id, 2, CreateTestState(id));
        }

        // Twice: a purge interrupted after this step runs it again.
        await store.DeleteInstanceAsync(purged);
        await store.DeleteInstanceAsync(purged);

        // The id reads like one that never existed; the other instance is untouched.
        Assert.HasCount(0, await store.ReadEventsAsync(purged, afterVersion: 0));
        Assert.AreEqual((null, 0), await store.ReadSnapshotAsync(purged));
        Assert.HasCount(2, await store.ReadEventsAsync(other, afterVersion: 0));
        Assert.AreEqual(2, (await store.ReadSnapshotAsync(other)).Version);
    }

    // ── MaxEventsPerLoad guard ───────────────────────────────────────────────

    [TestMethod]
    public async Task ReadEvents_AtCap_ReturnsAllEvents()
    {
        const int cap = 5;
        var store = await CreateStoreAsync(maxEventsPerLoad: cap);
        var instanceId = Guid.NewGuid();
        await store.AppendEventsAsync(instanceId, ExecutionStartedEvents(cap), expectedVersion: 0, batchId: Guid.NewGuid());

        Assert.HasCount(cap, await store.ReadEventsAsync(instanceId, afterVersion: 0));
    }

    [TestMethod]
    public async Task ReadEvents_OneOverCap_ThrowsInvalidOperation()
    {
        const int cap = 5;
        var store = await CreateStoreAsync(maxEventsPerLoad: cap);
        var instanceId = Guid.NewGuid();
        await store.AppendEventsAsync(instanceId, ExecutionStartedEvents(cap + 1), expectedVersion: 0, batchId: Guid.NewGuid());

        var caught = await CatchAsync<InvalidOperationException>(() => store.ReadEventsAsync(instanceId, afterVersion: 0));

        Assert.IsNotNull(caught, "Expected InvalidOperationException was not thrown");
        StringAssert.Contains(caught.Message, instanceId.ToString());
        StringAssert.Contains(caught.Message, "afterVersion=0");
    }

    [TestMethod]
    public async Task ReadEvents_WithAfterVersion_RespectsCap()
    {
        // cap + offset events in all; afterVersion=offset leaves exactly cap events — no throw.
        const int cap = 5;
        const int offset = 3;
        var store = await CreateStoreAsync(maxEventsPerLoad: cap);
        var instanceId = Guid.NewGuid();
        await store.AppendEventsAsync(instanceId, ExecutionStartedEvents(offset), expectedVersion: 0, batchId: Guid.NewGuid());
        await store.AppendEventsAsync(instanceId, ExecutionStartedEvents(cap), expectedVersion: offset, batchId: Guid.NewGuid());

        Assert.HasCount(cap, await store.ReadEventsAsync(instanceId, afterVersion: offset));
    }

    [TestMethod]
    public async Task ReadEvents_UnboundedWhenCapSetHigh_NoThrow()
    {
        var store = await CreateStoreAsync(maxEventsPerLoad: int.MaxValue);
        var instanceId = Guid.NewGuid();
        await store.AppendEventsAsync(instanceId, ExecutionStartedEvents(3), expectedVersion: 0, batchId: Guid.NewGuid());

        Assert.HasCount(3, await store.ReadEventsAsync(instanceId, afterVersion: 0));
    }

    private static List<WorkflowEvent> ExecutionStartedEvents(int count) =>
        Enumerable.Range(0, count).Select(_ => (WorkflowEvent)new ExecutionStarted()).ToList();

    private static async Task<TException?> CatchAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException ex) { return ex; }
        return null;
    }

    private static WorkflowInstanceState CreateTestState(Guid id)
    {
        var state = new WorkflowInstanceState();
        state.Initialize(id, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        state.Start(DateTimeOffset.UtcNow);
        return state;
    }
}
