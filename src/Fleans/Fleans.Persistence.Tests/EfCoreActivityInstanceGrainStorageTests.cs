using Fleans.Domain.States;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EfCoreActivityInstanceGrainStorageTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreActivityInstanceGrainStorage _storage = null!;
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
        _storage = new EfCoreActivityInstanceGrainStorage(_dbContextFactory);

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
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        state.State.SetVariablesId(Guid.NewGuid());

        await _storage.WriteStateAsync(StateName, grainId, state);
        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual("act-1", readState.State.ActivityId);
        Assert.AreEqual("ScriptTask", readState.State.ActivityType);
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task Write_WithCorrectETag_Succeeds()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        await _storage.WriteStateAsync(StateName, grainId, state);
        var firstETag = state.ETag;

        state.State.Execute();
        await _storage.WriteStateAsync(StateName, grainId, state);

        Assert.AreNotEqual(firstETag, state.ETag);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsTrue(readState.State.IsExecuting);
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Simulate a second writer that loaded state via ReadState (like Orleans does)
        var concurrentState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Execute();
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        // Original writer tries with stale ETag
        state.State.Execute();
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task FirstWrite_WithoutETag_Succeeds()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        Assert.IsNull(state.ETag);

        await _storage.WriteStateAsync(StateName, grainId, state);

        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);
    }

    [TestMethod]
    public async Task ErrorState_RoundTrip_NonNull()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        state.State.Fail(new InvalidOperationException("something broke"));

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.ErrorState);
        Assert.AreEqual(500, readState.State.ErrorState.Code);
        Assert.AreEqual("something broke", readState.State.ErrorState.Message);
    }

    [TestMethod]
    public async Task ErrorState_RoundTrip_Null()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNull(readState.State.ErrorState);
    }

    [TestMethod]
    public async Task Clear_RemovesState_SubsequentReadReturnsDefault()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);
        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsNull(readState.State.ActivityId);
        Assert.IsNull(readState.ETag);
        Assert.IsFalse(readState.RecordExists);
    }

    [TestMethod]
    public async Task Clear_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Simulate concurrent writer that loaded state via ReadState
        var concurrentState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Execute();
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        // Original caller tries to clear with stale ETag
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.ClearStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task DifferentGrainIds_AreIsolated()
    {
        var grainId1 = NewGrainId();
        var grainId2 = NewGrainId();

        var state1 = CreateGrainState();
        state1.State.SetActivity("act-1", "ScriptTask");
        var state2 = CreateGrainState();
        state2.State.SetActivity("act-2", "UserTask");

        await _storage.WriteStateAsync(StateName, grainId1, state1);
        await _storage.WriteStateAsync(StateName, grainId2, state2);

        var read1 = CreateGrainState();
        var read2 = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId1, read1);
        await _storage.ReadStateAsync(StateName, grainId2, read2);

        Assert.AreEqual("act-1", read1.State.ActivityId);
        Assert.AreEqual("act-2", read2.State.ActivityId);
    }

    [TestMethod]
    public async Task Timestamps_ArePreserved()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        state.State.Execute();
        state.State.Complete();

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.CreatedAt);
        Assert.IsNotNull(readState.State.ExecutionStartedAt);
        Assert.IsNotNull(readState.State.CompletedAt);
    }

    [TestMethod]
    public async Task Update_OverwritesCorrectly()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Update the state
        state.State.Execute();
        var variablesId = Guid.NewGuid();
        state.State.SetVariablesId(variablesId);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsTrue(readState.State.IsExecuting);
        Assert.AreEqual(variablesId, readState.State.VariablesId);
    }

    [TestMethod]
    public async Task Update_AddsErrorState()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Now fail the activity
        state.State.Fail(new InvalidOperationException("oops"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.ErrorState);
        Assert.AreEqual(500, readState.State.ErrorState.Code);
        Assert.AreEqual("oops", readState.State.ErrorState.Message);
    }

    [TestMethod]
    public async Task Update_ClearsErrorState()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        state.State.Fail(new InvalidOperationException("oops"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Re-execute clears error
        state.State.Execute();
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNull(readState.State.ErrorState);
    }

    [TestMethod]
    public async Task Update_OverwritesErrorState()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        state.State.Fail(new InvalidOperationException("first error"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        // Overwrite with a different error
        state.State.Fail(new ArgumentException("second error"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.ErrorState);
        Assert.AreEqual(500, readState.State.ErrorState.Code);
        Assert.AreEqual("second error", readState.State.ErrorState.Message);
    }

    [TestMethod]
    public async Task Write_WithStaleETagToNonExistentKey_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        state.ETag = "stale-etag";

        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
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
    public async Task Read_NonExistentKey_ReturnsDefaultState()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();

        await _storage.ReadStateAsync(StateName, grainId, state);

        Assert.IsNull(state.State.ActivityId);
        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [TestMethod]
    public async Task WriteClearWrite_ReCreatesSameGrainId()
    {
        var grainId = NewGrainId();
        var state = CreateGrainState();
        state.State.SetActivity("act-1", "ScriptTask");
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);

        // Re-create with same grain ID
        var newState = CreateGrainState();
        newState.State.SetActivity("act-2", "UserTask");
        await _storage.WriteStateAsync(StateName, grainId, newState);

        var readState = CreateGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual("act-2", readState.State.ActivityId);
        Assert.AreEqual("UserTask", readState.State.ActivityType);
        Assert.IsTrue(readState.RecordExists);
    }

    private static GrainId NewGrainId()
        => GrainId.Create("activityInstance", Guid.NewGuid().ToString("N"));

    private static TestGrainState<ActivityInstanceState> CreateGrainState()
        => new() { State = new ActivityInstanceState() };

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
