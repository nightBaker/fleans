using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Fleans.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

/// <summary>
/// Mirrors the grain-storage test suites against a real PostgreSQL instance.
/// Skipped when <c>FLEANS_PG_TESTS</c> env var is not set.
/// </summary>

// ---------------------------------------------------------------------------
// Message correlation grain storage
// ---------------------------------------------------------------------------

[TestClass]
[TestCategory("Postgres")]
public class EfCoreMessageCorrelationGrainStoragePostgresTests
{
    private static NpgsqlDataSource? s_dataSource;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreMessageCorrelationGrainStorage _storage = null!;
    private const string StateName = "state";

    [ClassInitialize]
    public static async Task ClassSetup(TestContext _)
    {
        if (!PostgresContainerFixture.IsEnabled) return;
        s_dataSource = NpgsqlDataSource.Create(PostgresContainerFixture.ConnectionString);
        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansPostgres(s_dataSource).Options;
        await using var db = new FleanCommandDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (s_dataSource is not null) await s_dataSource.DisposeAsync();
    }

    [TestInitialize]
    public async Task Setup()
    {
        if (!PostgresContainerFixture.IsEnabled)
        {
            Assert.Inconclusive("Set FLEANS_PG_TESTS=1 to run PostgreSQL tests.");
            return;
        }
        await using var conn = await s_dataSource!.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"TRUNCATE TABLE ""MessageCorrelations"", ""MessageSubscriptions"" RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();

        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansPostgres(s_dataSource!).Options;
        _dbContextFactory = new TestDbContextFactory(options);
        _storage = new EfCoreMessageCorrelationGrainStorage(_dbContextFactory);
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_ReturnsStoredState()
    {
        var grainId = NewGrainId("paymentReceived/order-123");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "waitPayment", Guid.NewGuid(), "order-123"));

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.Subscription);
        Assert.AreEqual("order-123", readState.State.Subscription.CorrelationKey);
        Assert.AreEqual("waitPayment", readState.State.Subscription.ActivityId);
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId("staleMsg/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrent = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrent);
        concurrent.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act2", Guid.NewGuid(), "key1");
        await _storage.WriteStateAsync(StateName, grainId, concurrent);

        state.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act3", Guid.NewGuid(), "key1");
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Clear_RemovesState()
    {
        var grainId = NewGrainId("clearMsg/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsNull(readState.State.Subscription);
        Assert.IsFalse(readState.RecordExists);
    }

    private static GrainId NewGrainId(string key) => GrainId.Create("messagecorrelation", key);

    private static TestGrainState<MessageCorrelationState> CreateGrainState(MessageSubscription sub)
        => new() { State = new MessageCorrelationState { Subscription = sub } };

    private static TestGrainState<MessageCorrelationState> CreateEmptyGrainState()
        => new() { State = new MessageCorrelationState() };
}

// ---------------------------------------------------------------------------
// Signal correlation grain storage
// ---------------------------------------------------------------------------

[TestClass]
[TestCategory("Postgres")]
public class EfCoreSignalCorrelationGrainStoragePostgresTests
{
    private static NpgsqlDataSource? s_dataSource;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreSignalCorrelationGrainStorage _storage = null!;
    private const string StateName = "state";

    [ClassInitialize]
    public static async Task ClassSetup(TestContext _)
    {
        if (!PostgresContainerFixture.IsEnabled) return;
        s_dataSource = NpgsqlDataSource.Create(PostgresContainerFixture.ConnectionString);
        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansPostgres(s_dataSource).Options;
        await using var db = new FleanCommandDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (s_dataSource is not null) await s_dataSource.DisposeAsync();
    }

    [TestInitialize]
    public async Task Setup()
    {
        if (!PostgresContainerFixture.IsEnabled)
        {
            Assert.Inconclusive("Set FLEANS_PG_TESTS=1 to run PostgreSQL tests.");
            return;
        }
        await using var conn = await s_dataSource!.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"TRUNCATE TABLE ""SignalCorrelations"", ""SignalSubscriptions"" RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();

        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansPostgres(s_dataSource!).Options;
        _dbContextFactory = new TestDbContextFactory(options);
        _storage = new EfCoreSignalCorrelationGrainStorage(_dbContextFactory);
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_PreservesSubscriptions()
    {
        var grainId = NewGrainId("approvalSignal");
        var sub1 = new SignalSubscription(Guid.NewGuid(), "waitApproval", Guid.NewGuid());
        var sub2 = new SignalSubscription(Guid.NewGuid(), "waitConfirm", Guid.NewGuid());
        var state = CreateGrainState(sub1, sub2);

        await _storage.WriteStateAsync(StateName, grainId, state);
        Assert.IsNotNull(state.ETag);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(2, readState.State.Subscriptions.Count);
        Assert.AreEqual("waitApproval", readState.State.Subscriptions[0].ActivityId);
        Assert.AreEqual("waitConfirm", readState.State.Subscriptions[1].ActivityId);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId("staleSig");
        var state = CreateGrainState(new SignalSubscription(Guid.NewGuid(), "act1", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrent = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrent);
        concurrent.State.Subscriptions.Add(new SignalSubscription(Guid.NewGuid(), "act2", Guid.NewGuid()));
        await _storage.WriteStateAsync(StateName, grainId, concurrent);

        state.State.Subscriptions.Add(new SignalSubscription(Guid.NewGuid(), "act3", Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    private static GrainId NewGrainId(string key) => GrainId.Create("signalcorrelation", key);

    private static TestGrainState<SignalCorrelationState> CreateGrainState(
        params SignalSubscription[] subs)
    {
        var state = new TestGrainState<SignalCorrelationState> { State = new SignalCorrelationState() };
        foreach (var sub in subs)
            state.State.Subscriptions.Add(sub);
        return state;
    }

    private static TestGrainState<SignalCorrelationState> CreateEmptyGrainState()
        => new() { State = new SignalCorrelationState() };
}

// ---------------------------------------------------------------------------
// Process definition grain storage
// ---------------------------------------------------------------------------

[TestClass]
[TestCategory("Postgres")]
public class EfCoreProcessDefinitionGrainStoragePostgresTests
{
    private static NpgsqlDataSource? s_dataSource;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreProcessDefinitionGrainStorage _storage = null!;
    private const string StateName = "state";

    [ClassInitialize]
    public static async Task ClassSetup(TestContext _)
    {
        if (!PostgresContainerFixture.IsEnabled) return;
        s_dataSource = NpgsqlDataSource.Create(PostgresContainerFixture.ConnectionString);
        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansPostgres(s_dataSource).Options;
        await using var db = new FleanCommandDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (s_dataSource is not null) await s_dataSource.DisposeAsync();
    }

    [TestInitialize]
    public async Task Setup()
    {
        if (!PostgresContainerFixture.IsEnabled)
        {
            Assert.Inconclusive("Set FLEANS_PG_TESTS=1 to run PostgreSQL tests.");
            return;
        }
        await using var conn = await s_dataSource!.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        // ProcessDefinitions has no FK references from other tables that EnsureCreated can create
        // without WorkflowInstances, so truncate with CASCADE to handle any dependents.
        cmd.CommandText = @"TRUNCATE TABLE ""ProcessDefinitions"" RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();

        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansPostgres(s_dataSource!).Options;
        _dbContextFactory = new TestDbContextFactory(options);
        _storage = new EfCoreProcessDefinitionGrainStorage(_dbContextFactory);
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_ReturnsScalarFields()
    {
        var id = "myProcess:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainState(id, "myProcess", 1);

        await _storage.WriteStateAsync(StateName, grainId, state);
        Assert.IsNotNull(state.ETag);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.AreEqual(id, readState.State.ProcessDefinitionId);
        Assert.AreEqual("myProcess", readState.State.ProcessDefinitionKey);
        Assert.AreEqual(1, readState.State.Version);
        Assert.AreEqual("<bpmn/>", readState.State.BpmnXml);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task WriteAndRead_WorkflowJson_PolymorphicActivitiesPreserved()
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
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var id = "stale:1:ts";
        var grainId = NewGrainId(id);
        var state = CreateGrainState(id, "stale", 1);
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrent = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrent);
        concurrent.State = WithBpmnXml(concurrent.State, "<concurrent/>");
        await _storage.WriteStateAsync(StateName, grainId, concurrent);

        state.State = WithBpmnXml(state.State, "<stale/>");
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

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsNull(readState.ETag);
        Assert.IsFalse(readState.RecordExists);
    }

    private static GrainId NewGrainId(string key) => GrainId.Create("processDefinition", key);

    private static TestGrainState<ProcessDefinition> CreateGrainState(string id, string key, int version)
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

    private static ProcessDefinition WithBpmnXml(ProcessDefinition source, string bpmnXml) => new()
    {
        ProcessDefinitionId = source.ProcessDefinitionId,
        ProcessDefinitionKey = source.ProcessDefinitionKey,
        Version = source.Version,
        DeployedAt = source.DeployedAt,
        Workflow = source.Workflow,
        BpmnXml = bpmnXml
    };

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
