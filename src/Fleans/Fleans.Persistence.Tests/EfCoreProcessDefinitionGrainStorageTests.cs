using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EfCoreProcessDefinitionGrainStorageTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreProcessDefinitionGrainStorage _storage = null!;
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
        _storage = new EfCoreProcessDefinitionGrainStorage(_dbContextFactory);

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
        var id = "myProcess:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainState(id, "myProcess", 1);
        var deployedAt = state.State.DeployedAt;

        await _storage.WriteStateAsync(StateName, grainId, state);
        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(id, readState.State.ProcessDefinitionId);
        Assert.AreEqual("myProcess", readState.State.ProcessDefinitionKey);
        Assert.AreEqual(1, readState.State.Version);
        Assert.AreEqual(deployedAt, readState.State.DeployedAt);
        Assert.AreEqual("<bpmn/>", readState.State.BpmnXml);
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_WorkflowJsonPreserved()
    {
        var id = "poly:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainStateWithMixedActivities(id);

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var workflow = readState.State.Workflow;
        Assert.AreEqual(4, workflow.Activities.Count);
        Assert.IsInstanceOfType<StartEvent>(workflow.Activities[0]);
        Assert.IsInstanceOfType<ScriptTask>(workflow.Activities[1]);
        Assert.IsInstanceOfType<ExclusiveGateway>(workflow.Activities[2]);
        Assert.IsInstanceOfType<EndEvent>(workflow.Activities[3]);

        Assert.AreEqual(4, workflow.SequenceFlows.Count);
        Assert.IsInstanceOfType<ConditionalSequenceFlow>(workflow.SequenceFlows[1]);
        Assert.IsInstanceOfType<DefaultSequenceFlow>(workflow.SequenceFlows[3]);
    }

    [TestMethod]
    public async Task Write_WithCorrectETag_Succeeds()
    {
        var id = "upd:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainState(id, "upd", 1);
        await _storage.WriteStateAsync(StateName, grainId, state);
        var firstETag = state.ETag;

        state.State = WithBpmnXml(state.State, "<bpmn updated/>");
        await _storage.WriteStateAsync(StateName, grainId, state);

        Assert.AreNotEqual(firstETag, state.ETag);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.AreEqual("<bpmn updated/>", readState.State.BpmnXml);
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var id = "stale:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainState(id, "stale", 1);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State = WithBpmnXml(concurrentState.State, "<concurrent/>");
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        state.State = WithBpmnXml(state.State, "<stale/>");
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task FirstWrite_WithoutETag_Succeeds()
    {
        var id = "first:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainState(id, "first", 1);
        Assert.IsNull(state.ETag);

        await _storage.WriteStateAsync(StateName, grainId, state);

        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);
    }

    [TestMethod]
    public async Task Write_WithStaleETagToNonExistentKey_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId("nope:1:ts");
        var state = CreateGrainState("nope:1:ts", "nope", 1);
        state.ETag = "stale-etag";

        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Clear_RemovesState_SubsequentReadReturnsDefault()
    {
        var id = "clr:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainState(id, "clr", 1);
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
        var id = "clrstale:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainState(id, "clrstale", 1);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State = WithBpmnXml(concurrentState.State, "<concurrent/>");
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.ClearStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Clear_NonExistentGrain_IsNoOp()
    {
        var grainId = NewGrainId("noop:1:ts");
        var state = CreateEmptyGrainState();

        await _storage.ClearStateAsync(StateName, grainId, state);

        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [TestMethod]
    public async Task Read_NonExistentKey_LeavesStateUnchanged()
    {
        var grainId = NewGrainId("missing:1:ts");
        var state = CreateEmptyGrainState();

        await _storage.ReadStateAsync(StateName, grainId, state);

        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [TestMethod]
    public async Task DifferentGrainIds_AreIsolated()
    {
        var id1 = "iso1:1:ts";
        var id2 = "iso2:1:ts";
        var grainId1 = NewGrainId(id1);
        var grainId2 = NewGrainId(id2);

        var state1 = CreateGrainState(id1, "iso1", 1);
        var state2 = CreateGrainState(id2, "iso2", 2);

        await _storage.WriteStateAsync(StateName, grainId1, state1);
        await _storage.WriteStateAsync(StateName, grainId2, state2);

        var read1 = CreateEmptyGrainState();
        var read2 = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId1, read1);
        await _storage.ReadStateAsync(StateName, grainId2, read2);

        Assert.AreEqual("iso1", read1.State.ProcessDefinitionKey);
        Assert.AreEqual(1, read1.State.Version);
        Assert.AreEqual("iso2", read2.State.ProcessDefinitionKey);
        Assert.AreEqual(2, read2.State.Version);
    }

    [TestMethod]
    public async Task WriteClearWrite_ReCreatesSameGrainId()
    {
        var id = "recreate:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainState(id, "recreate", 1);
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);

        var newState = CreateGrainState(id, "recreate", 2);
        await _storage.WriteStateAsync(StateName, grainId, newState);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual("recreate", readState.State.ProcessDefinitionKey);
        Assert.AreEqual(2, readState.State.Version);
        Assert.IsTrue(readState.RecordExists);
    }

    private static ProcessDefinition WithBpmnXml(ProcessDefinition source, string bpmnXml) => new()
    {
        ProcessDefinitionId = source.ProcessDefinitionId,
        ProcessDefinitionKey = source.ProcessDefinitionKey,
        Version = source.Version,
        DeployedAt = source.DeployedAt,
        Workflow = source.Workflow,
        BpmnXml = bpmnXml
    };

    private static GrainId NewGrainId(string key)
        => GrainId.Create("processDefinition", key);

    private static TestGrainState<ProcessDefinition> CreateGrainState(
        string id, string key, int version)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var flow = new SequenceFlow("flow1", start, end);

        return new TestGrainState<ProcessDefinition>
        {
            State = new ProcessDefinition
            {
                ProcessDefinitionId = id,
                ProcessDefinitionKey = key,
                Version = version,
                DeployedAt = DateTimeOffset.UtcNow,
                BpmnXml = "<bpmn/>",
                Workflow = new WorkflowDefinition
                {
                    WorkflowId = key,
                    ProcessDefinitionId = id,
                    Activities = [start, end],
                    SequenceFlows = [flow]
                }
            }
        };
    }

    private static TestGrainState<ProcessDefinition> CreateGrainStateWithMixedActivities(string id)
    {
        var start = new StartEvent("start");
        var script = new ScriptTask("script1", "return 42;", "csharp");
        var gateway = new ExclusiveGateway("gw1");
        var end = new EndEvent("end");

        var flow1 = new SequenceFlow("flow1", start, script);
        var condFlow1 = new ConditionalSequenceFlow("condFlow1", gateway, end, "x > 10");
        var condFlow2 = new ConditionalSequenceFlow("condFlow2", gateway, script, "x <= 10");
        var defaultFlow = new DefaultSequenceFlow("defaultFlow", gateway, end);

        return new TestGrainState<ProcessDefinition>
        {
            State = new ProcessDefinition
            {
                ProcessDefinitionId = id,
                ProcessDefinitionKey = "poly",
                Version = 1,
                DeployedAt = DateTimeOffset.UtcNow,
                BpmnXml = "<bpmn:definitions/>",
                Workflow = new WorkflowDefinition
                {
                    WorkflowId = "poly",
                    ProcessDefinitionId = id,
                    Activities = [start, script, gateway, end],
                    SequenceFlows = [flow1, condFlow1, condFlow2, defaultFlow]
                }
            }
        };
    }

    private static TestGrainState<ProcessDefinition> CreateEmptyGrainState()
        => new()
        {
            State = new ProcessDefinition
            {
                ProcessDefinitionId = string.Empty,
                ProcessDefinitionKey = string.Empty,
                Version = 0,
                DeployedAt = default,
                BpmnXml = string.Empty,
                Workflow = new WorkflowDefinition
                {
                    WorkflowId = string.Empty,
                    Activities = [],
                    SequenceFlows = []
                }
            }
        };

}
