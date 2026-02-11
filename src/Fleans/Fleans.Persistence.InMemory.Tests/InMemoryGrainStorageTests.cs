using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.InMemory.Tests;

[TestClass]
public class InMemoryGrainStorageTests
{
    private InMemoryGrainStorage _storage = null!;
    private GrainId _grainId;
    private const string StateName = "state";

    [TestInitialize]
    public void Setup()
    {
        _storage = new InMemoryGrainStorage();
        _grainId = GrainId.Create("test", Guid.NewGuid().ToString());
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_ReturnsStoredState()
    {
        var state = new TestGrainState<TestState> { State = new TestState("hello") };

        await _storage.WriteStateAsync(StateName, _grainId, state);
        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);

        var readState = new TestGrainState<TestState>();
        await _storage.ReadStateAsync(StateName, _grainId, readState);

        Assert.AreEqual("hello", readState.State.Value);
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task Write_WithCorrectETag_Succeeds()
    {
        var state = new TestGrainState<TestState> { State = new TestState("v1") };
        await _storage.WriteStateAsync(StateName, _grainId, state);
        var firstETag = state.ETag;

        state.State = new TestState("v2");
        await _storage.WriteStateAsync(StateName, _grainId, state);

        Assert.AreNotEqual(firstETag, state.ETag);

        var readState = new TestGrainState<TestState>();
        await _storage.ReadStateAsync(StateName, _grainId, readState);
        Assert.AreEqual("v2", readState.State.Value);
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var state = new TestGrainState<TestState> { State = new TestState("v1") };
        await _storage.WriteStateAsync(StateName, _grainId, state);

        // Simulate a second writer that updates the state
        var concurrentState = new TestGrainState<TestState> { State = new TestState("v2"), ETag = state.ETag };
        await _storage.WriteStateAsync(StateName, _grainId, concurrentState);

        // Original writer tries to write with stale ETag
        state.State = new TestState("v3");
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, _grainId, state));
    }

    [TestMethod]
    public async Task FirstWrite_WithoutETag_Succeeds()
    {
        var state = new TestGrainState<TestState> { State = new TestState("first") };
        Assert.IsNull(state.ETag);

        await _storage.WriteStateAsync(StateName, _grainId, state);

        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);
    }

    [TestMethod]
    public async Task Clear_RemovesState_SubsequentReadReturnsDefault()
    {
        var state = new TestGrainState<TestState> { State = new TestState("data") };
        await _storage.WriteStateAsync(StateName, _grainId, state);

        await _storage.ClearStateAsync(StateName, _grainId, state);
        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);

        var readState = new TestGrainState<TestState>();
        await _storage.ReadStateAsync(StateName, _grainId, readState);
        Assert.IsNull(readState.State.Value);
        Assert.IsNull(readState.ETag);
        Assert.IsFalse(readState.RecordExists);
    }

    [TestMethod]
    public async Task Read_NonExistentKey_ReturnsDefaultState()
    {
        var state = new TestGrainState<TestState>();
        await _storage.ReadStateAsync(StateName, _grainId, state);

        Assert.IsNull(state.State.Value);
        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [TestMethod]
    public async Task DifferentGrainIds_AreIsolated()
    {
        var grainId1 = GrainId.Create("test", "grain1");
        var grainId2 = GrainId.Create("test", "grain2");

        var state1 = new TestGrainState<TestState> { State = new TestState("one") };
        var state2 = new TestGrainState<TestState> { State = new TestState("two") };

        await _storage.WriteStateAsync(StateName, grainId1, state1);
        await _storage.WriteStateAsync(StateName, grainId2, state2);

        var read1 = new TestGrainState<TestState>();
        var read2 = new TestGrainState<TestState>();
        await _storage.ReadStateAsync(StateName, grainId1, read1);
        await _storage.ReadStateAsync(StateName, grainId2, read2);

        Assert.AreEqual("one", read1.State.Value);
        Assert.AreEqual("two", read2.State.Value);
    }

    private class TestState
    {
        public string? Value { get; set; }
        public TestState() { }
        public TestState(string value) => Value = value;
    }

    private class TestGrainState<T> : IGrainState<T> where T : new()
    {
        public T State { get; set; } = new();
        public string? ETag { get; set; }
        public bool RecordExists { get; set; }
    }
}
