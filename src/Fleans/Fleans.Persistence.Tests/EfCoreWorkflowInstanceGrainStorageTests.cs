using System.Dynamic;
using Fleans.Domain.States;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EfCoreWorkflowInstanceGrainStorageTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreWorkflowInstanceGrainStorage _storage = null!;
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
        _storage = new EfCoreWorkflowInstanceGrainStorage(_dbContextFactory);

        using var db = _dbContextFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
    }

    // ───────────────────────────────────────────────
    // 1. Round-trip tests
    // ───────────────────────────────────────────────

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_ReturnsStoredState()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        await _storage.WriteStateAsync(StateName, grainId, state);
        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsTrue(readState.State.IsStarted);
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task WriteAndRead_WithActiveActivities_Preserves()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var entry1 = new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty);
        var entry2 = new ActivityInstanceEntry(Guid.NewGuid(), "act-2", Guid.Empty);
        state.State.AddEntries([entry1, entry2]);

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var activeActivities = readState.State.GetActiveActivities();
        Assert.AreEqual(2, activeActivities.Count());
        Assert.IsTrue(activeActivities.Any(a => a.ActivityId == "act-1"));
        Assert.IsTrue(activeActivities.Any(a => a.ActivityId == "act-2"));
    }

    [TestMethod]
    public async Task WriteAndRead_WithCompletedActivities_Preserves()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var entry1 = new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty);
        var entry2 = new ActivityInstanceEntry(Guid.NewGuid(), "act-2", Guid.Empty);
        state.State.AddEntries([entry1, entry2]);
        state.State.CompleteEntries([entry1, entry2]);

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var completedActivities = readState.State.GetCompletedActivities();
        Assert.AreEqual(2, completedActivities.Count());
        Assert.IsTrue(completedActivities.Any(a => a.ActivityId == "act-1"));
        Assert.IsTrue(completedActivities.Any(a => a.ActivityId == "act-2"));
    }

    [TestMethod]
    public async Task WriteAndRead_WithVariableStates_Preserves()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var variablesId = Guid.NewGuid();
        state.State.StartWith(new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty), variablesId);

        var vars = new ExpandoObject();
        var dict = (IDictionary<string, object>)vars;
        dict["name"] = "test";
        dict["count"] = 42;
        state.State.MergeState(variablesId, vars);

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var variableStates = readState.State.VariableStates;
        Assert.AreEqual(1, variableStates.Count);

        var readVars = variableStates.First();
        var readDict = (IDictionary<string, object>)readVars.Variables;
        Assert.AreEqual("test", readDict["name"]);
        // JSON deserialization may convert int to long or double depending on the JSON parser
        Assert.AreEqual(42, Convert.ToInt32(readDict["count"]));
    }

    [TestMethod]
    public async Task WriteAndRead_WithConditionSequenceStates_Preserves()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var gatewayId1 = Guid.NewGuid();
        var gatewayId2 = Guid.NewGuid();
        state.State.AddConditionSequenceStates(gatewayId1, ["seq-1", "seq-2"]);
        state.State.AddConditionSequenceStates(gatewayId2, ["seq-3"]);

        // Evaluate one condition
        state.State.SetConditionSequenceResult(gatewayId1, "seq-1", true);

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var conditionStates = readState.State.ConditionSequenceStates
            .GroupBy(c => c.GatewayActivityInstanceId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        Assert.AreEqual(2, conditionStates.Count);

        // Verify gateway 1 has 2 sequences
        Assert.IsTrue(conditionStates.ContainsKey(gatewayId1));
        var gw1Sequences = conditionStates[gatewayId1];
        Assert.AreEqual(2, gw1Sequences.Length);

        var seq1 = gw1Sequences.First(s => s.ConditionalSequenceFlowId == "seq-1");
        Assert.IsTrue(seq1.IsEvaluated);
        Assert.IsTrue(seq1.Result);

        var seq2 = gw1Sequences.First(s => s.ConditionalSequenceFlowId == "seq-2");
        Assert.IsFalse(seq2.IsEvaluated);
        Assert.IsFalse(seq2.Result);

        // Verify gateway 2 has 1 sequence
        Assert.IsTrue(conditionStates.ContainsKey(gatewayId2));
        var gw2Sequences = conditionStates[gatewayId2];
        Assert.AreEqual(1, gw2Sequences.Length);
        Assert.AreEqual("seq-3", gw2Sequences[0].ConditionalSequenceFlowId);
        Assert.IsFalse(gw2Sequences[0].IsEvaluated);
    }

    [TestMethod]
    public async Task Timestamps_ArePreserved()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();
        state.State.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        state.State.ExecutionStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        state.State.CompletedAt = DateTimeOffset.UtcNow;

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.CreatedAt);
        Assert.IsNotNull(readState.State.ExecutionStartedAt);
        Assert.IsNotNull(readState.State.CompletedAt);
    }

    // ───────────────────────────────────────────────
    // 2. ETag concurrency tests
    // ───────────────────────────────────────────────

    [TestMethod]
    public async Task Write_WithCorrectETag_Succeeds()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();
        await _storage.WriteStateAsync(StateName, grainId, state);
        var firstETag = state.ETag;

        state.State.Complete();
        await _storage.WriteStateAsync(StateName, grainId, state);

        Assert.AreNotEqual(firstETag, state.ETag);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsTrue(readState.State.IsCompleted);
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Simulate a second writer that loaded state via ReadState (like Orleans does)
        var concurrentState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Complete();
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        // Original writer tries with stale ETag
        state.State.Complete();
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Write_WithStaleETagToNonExistentKey_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();
        state.ETag = "stale-etag";

        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task FirstWrite_WithoutETag_Succeeds()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();
        Assert.IsNull(state.ETag);

        await _storage.WriteStateAsync(StateName, grainId, state);

        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);
    }

    // ───────────────────────────────────────────────
    // 3. Clear tests
    // ───────────────────────────────────────────────

    [TestMethod]
    public async Task Clear_RemovesState_SubsequentReadReturnsDefault()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var variablesId = Guid.NewGuid();
        state.State.StartWith(new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty), variablesId);
        state.State.AddConditionSequenceStates(Guid.NewGuid(), ["seq-1"]);

        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);
        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsFalse(readState.State.IsStarted);
        Assert.IsNull(readState.ETag);
        Assert.IsFalse(readState.RecordExists);
    }

    [TestMethod]
    public async Task Clear_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Simulate concurrent writer that loaded state via ReadState
        var concurrentState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Complete();
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        // Original caller tries to clear with stale ETag
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.ClearStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Clear_NonExistentGrain_IsNoOp()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();

        await _storage.ClearStateAsync(StateName, grainId, state);

        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [TestMethod]
    public async Task WriteClearWrite_ReCreatesSameGrainId()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);

        // Re-create with same grain ID
        var newState = CreateGrainState();
        newState.State.Start();
        newState.State.Complete();
        await _storage.WriteStateAsync(StateName, grainId, newState);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsTrue(readState.State.IsStarted);
        Assert.IsTrue(readState.State.IsCompleted);
        Assert.IsTrue(readState.RecordExists);
    }

    // ───────────────────────────────────────────────
    // 4. Child collection diffing tests
    // ───────────────────────────────────────────────

    [TestMethod]
    public async Task Update_AddsNewActiveActivity()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();
        state.State.AddEntries([new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty)]);
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Add another active activity
        state.State.AddEntries([new ActivityInstanceEntry(Guid.NewGuid(), "act-2", Guid.Empty)]);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(2, readState.State.GetActiveActivities().Count());
        Assert.IsTrue(readState.State.GetActiveActivities().Any(a => a.ActivityId == "act-1"));
        Assert.IsTrue(readState.State.GetActiveActivities().Any(a => a.ActivityId == "act-2"));
    }

    [TestMethod]
    public async Task Update_MovesActiveToCompleted()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty);
        state.State.AddEntries([entry]);
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Move from active to completed
        state.State.CompleteEntries([entry]);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(0, readState.State.GetActiveActivities().Count());
        Assert.AreEqual(1, readState.State.GetCompletedActivities().Count());
        Assert.AreEqual("act-1", readState.State.GetCompletedActivities().First().ActivityId);
    }

    [TestMethod]
    public async Task Update_RemovesActivity()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var entry1 = new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty);
        var entry2 = new ActivityInstanceEntry(Guid.NewGuid(), "act-2", Guid.Empty);
        state.State.AddEntries([entry1, entry2]);
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Remove one activity
        state.State.Entries.Remove(entry2);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(1, readState.State.GetActiveActivities().Count());
        Assert.AreEqual("act-1", readState.State.GetActiveActivities().First().ActivityId);
    }

    [TestMethod]
    public async Task Update_AddsVariableState()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var variablesId1 = Guid.NewGuid();
        state.State.StartWith(new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty), variablesId1);
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Add a second variable scope by cloning
        var variablesId2 = state.State.AddCloneOfVariableState(variablesId1);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(2, readState.State.VariableStates.Count);
    }

    [TestMethod]
    public async Task Update_UpdatesVariableValues()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var variablesId = Guid.NewGuid();
        state.State.StartWith(new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty), variablesId);

        var vars1 = new ExpandoObject();
        ((IDictionary<string, object>)vars1)["x"] = "hello";
        state.State.MergeState(variablesId, vars1);
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Merge new values
        var vars2 = new ExpandoObject();
        ((IDictionary<string, object>)vars2)["y"] = "world";
        state.State.MergeState(variablesId, vars2);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var readVars = readState.State.VariableStates.First();
        var readDict = (IDictionary<string, object>)readVars.Variables;
        Assert.AreEqual("hello", readDict["x"]);
        Assert.AreEqual("world", readDict["y"]);
    }

    [TestMethod]
    public async Task Update_AddsConditionSequenceStates()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Add conditions on update
        var gatewayId = Guid.NewGuid();
        state.State.AddConditionSequenceStates(gatewayId, ["seq-1", "seq-2"]);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var conditionStates = readState.State.ConditionSequenceStates
            .GroupBy(c => c.GatewayActivityInstanceId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        Assert.AreEqual(1, conditionStates.Count);
        Assert.IsTrue(conditionStates.ContainsKey(gatewayId));
        Assert.AreEqual(2, conditionStates[gatewayId].Length);
    }

    [TestMethod]
    public async Task Update_EvaluatesConditionSequence()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var gatewayId = Guid.NewGuid();
        state.State.AddConditionSequenceStates(gatewayId, ["seq-1", "seq-2"]);
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Evaluate conditions
        state.State.SetConditionSequenceResult(gatewayId, "seq-1", true);
        state.State.SetConditionSequenceResult(gatewayId, "seq-2", false);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var sequences = readState.State.GetConditionSequenceStatesForGateway(gatewayId).ToArray();
        var seq1 = sequences.First(s => s.ConditionalSequenceFlowId == "seq-1");
        Assert.IsTrue(seq1.IsEvaluated);
        Assert.IsTrue(seq1.Result);

        var seq2 = sequences.First(s => s.ConditionalSequenceFlowId == "seq-2");
        Assert.IsTrue(seq2.IsEvaluated);
        Assert.IsFalse(seq2.Result);
    }

    [TestMethod]
    public async Task Update_RemovesConditionSequenceStates()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.Start();

        var gatewayId = Guid.NewGuid();
        state.State.AddConditionSequenceStates(gatewayId, ["seq-1", "seq-2"]);
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Remove the condition states for this gateway
        state.State.ConditionSequenceStates.RemoveAll(c => c.GatewayActivityInstanceId == gatewayId);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(0, readState.State.ConditionSequenceStates.Count);
    }

    // ───────────────────────────────────────────────
    // 5. Isolation test
    // ───────────────────────────────────────────────

    [TestMethod]
    public async Task DifferentGrainIds_AreIsolated()
    {
        var grainId1 = NewGrainId();
        var grainId2 = NewGrainId();

        var state1 = CreateGrainState();
        state1.State.Start();
        state1.State.AddEntries([new ActivityInstanceEntry(Guid.NewGuid(), "act-1", Guid.Empty)]);

        var state2 = CreateGrainState();
        state2.State.Start();
        state2.State.Complete();
        var completedEntry = new ActivityInstanceEntry(Guid.NewGuid(), "act-2", Guid.Empty);
        state2.State.AddEntries([completedEntry]);
        state2.State.CompleteEntries([completedEntry]);

        await _storage.WriteStateAsync(StateName, grainId1, state1);
        await _storage.WriteStateAsync(StateName, grainId2, state2);

        var read1 = CreateGrainState();
        var read2 = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId1, read1);
        await _storage.ReadStateAsync(StateName, grainId2, read2);

        // Grain 1: started, not completed, 1 active activity
        Assert.IsTrue(read1.State.IsStarted);
        Assert.IsFalse(read1.State.IsCompleted);
        Assert.AreEqual(1, read1.State.GetActiveActivities().Count());
        Assert.AreEqual("act-1", read1.State.GetActiveActivities().First().ActivityId);
        Assert.AreEqual(0, read1.State.GetCompletedActivities().Count());

        // Grain 2: started and completed, 1 completed activity
        Assert.IsTrue(read2.State.IsStarted);
        Assert.IsTrue(read2.State.IsCompleted);
        Assert.AreEqual(0, read2.State.GetActiveActivities().Count());
        Assert.AreEqual(1, read2.State.GetCompletedActivities().Count());
        Assert.AreEqual("act-2", read2.State.GetCompletedActivities().First().ActivityId);
    }

    // ───────────────────────────────────────────────
    // 6. Complex state test
    // ───────────────────────────────────────────────

    [TestMethod]
    public async Task FullWorkflowLifecycle_RoundTrip()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();

        // Start the workflow
        state.State.Start();
        state.State.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        state.State.ExecutionStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Add active activities with a variable scope
        var variablesId = Guid.NewGuid();
        var entry1 = new ActivityInstanceEntry(Guid.NewGuid(), "startEvent-1", Guid.Empty);
        state.State.StartWith(entry1, variablesId);

        var entry2 = new ActivityInstanceEntry(Guid.NewGuid(), "task-1", Guid.Empty);
        var entry3 = new ActivityInstanceEntry(Guid.NewGuid(), "gateway-1", Guid.Empty);
        state.State.AddEntries([entry2, entry3]);

        // Set variables
        var vars = new ExpandoObject();
        var dict = (IDictionary<string, object>)vars;
        dict["processName"] = "TestProcess";
        dict["iteration"] = 1;
        dict["isActive"] = true;
        state.State.MergeState(variablesId, vars);

        // Add condition sequences for the gateway
        var gatewayInstanceId = entry3.ActivityInstanceId;
        state.State.AddConditionSequenceStates(gatewayInstanceId, ["cond-seq-1", "cond-seq-2"]);

        // Evaluate one condition
        state.State.SetConditionSequenceResult(gatewayInstanceId, "cond-seq-1", true);

        // Move startEvent from active to completed
        state.State.CompleteEntries([entry1]);

        // Clone variable state for the gateway branch
        var clonedVarsId = state.State.AddCloneOfVariableState(variablesId);

        // Complete the workflow
        state.State.CompleteEntries([entry2, entry3]);
        state.State.Complete();
        state.State.CompletedAt = DateTimeOffset.UtcNow;

        // Write the full state
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Read it back
        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        // Verify main scalars
        Assert.IsTrue(readState.State.IsStarted);
        Assert.IsTrue(readState.State.IsCompleted);
        Assert.IsNotNull(readState.State.CreatedAt);
        Assert.IsNotNull(readState.State.ExecutionStartedAt);
        Assert.IsNotNull(readState.State.CompletedAt);

        // Verify activities
        Assert.AreEqual(0, readState.State.GetActiveActivities().Count());
        Assert.AreEqual(3, readState.State.GetCompletedActivities().Count());
        Assert.IsTrue(readState.State.GetCompletedActivities().Any(a => a.ActivityId == "startEvent-1"));
        Assert.IsTrue(readState.State.GetCompletedActivities().Any(a => a.ActivityId == "task-1"));
        Assert.IsTrue(readState.State.GetCompletedActivities().Any(a => a.ActivityId == "gateway-1"));

        // Verify variable states (2 scopes: original + cloned)
        var readVarStates = readState.State.VariableStates;
        Assert.AreEqual(2, readVarStates.Count);

        // At least one scope should have our variables
        var scopeWithVars = readVarStates.First(vs =>
        {
            var d = (IDictionary<string, object>)vs.Variables;
            return d.ContainsKey("processName");
        });
        var readDict = (IDictionary<string, object>)scopeWithVars.Variables;
        Assert.AreEqual("TestProcess", readDict["processName"]);
        Assert.AreEqual(1, Convert.ToInt32(readDict["iteration"]));
        Assert.AreEqual(true, readDict["isActive"]);

        // Verify condition sequence states
        var condStates = readState.State.ConditionSequenceStates
            .GroupBy(c => c.GatewayActivityInstanceId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        Assert.AreEqual(1, condStates.Count);
        Assert.IsTrue(condStates.ContainsKey(gatewayInstanceId));
        var sequences = condStates[gatewayInstanceId];
        Assert.AreEqual(2, sequences.Length);

        var evaluatedSeq = sequences.First(s => s.ConditionalSequenceFlowId == "cond-seq-1");
        Assert.IsTrue(evaluatedSeq.IsEvaluated);
        Assert.IsTrue(evaluatedSeq.Result);

        var unevaluatedSeq = sequences.First(s => s.ConditionalSequenceFlowId == "cond-seq-2");
        Assert.IsFalse(unevaluatedSeq.IsEvaluated);
        Assert.IsFalse(unevaluatedSeq.Result);

        // Verify ETag
        Assert.IsNotNull(readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    // ───────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────

    private static GrainId NewGrainId()
        => GrainId.Create("workflowInstance", Guid.NewGuid().ToString("N"));

    private static TestGrainState<WorkflowInstanceState> CreateGrainState()
        => new() { State = new WorkflowInstanceState() };

    private class TestGrainState<T> : IGrainState<T> where T : new()
    {
        public T State { get; set; } = new();
        public string? ETag { get; set; }
        public bool RecordExists { get; set; }
    }

    private class TestDbContextFactory : IDbContextFactory<FleanCommandDbContext>
    {
        private readonly DbContextOptions<FleanCommandDbContext> _options;

        public TestDbContextFactory(DbContextOptions<FleanCommandDbContext> options)
        {
            _options = options;
        }

        public FleanCommandDbContext CreateDbContext() => new(_options);

        public Task<FleanCommandDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
