using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Persistence;
using Fleans.Domain.Sequences;
using Fleans.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleans.Persistence.Tests;

/// <summary>
/// Mirrors <see cref="EfCoreProcessDefinitionRepositoryTests"/> against a real PostgreSQL instance
/// (Testcontainers). Skipped when <c>FLEANS_PG_TESTS</c> env var is not set.
/// </summary>
[TestClass]
[TestCategory("Postgres")]
public class EfCoreProcessDefinitionRepositoryPostgresTests
{
    private static NpgsqlDataSource? s_dataSource;

    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private IDbContextFactory<FleanQueryDbContext> _queryDbContextFactory = null!;
    private IProcessDefinitionRepository _repository = null!;

    [ClassInitialize]
    public static async Task ClassSetup(TestContext _)
    {
        if (!PostgresContainerFixture.IsEnabled) return;

        s_dataSource = NpgsqlDataSource.Create(PostgresContainerFixture.ConnectionString);

        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansPostgres(s_dataSource)
            .Options;

        await using var db = new FleanCommandDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (s_dataSource is not null)
            await s_dataSource.DisposeAsync();
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
        // Truncate ProcessDefinitions and all dependent tables
        cmd.CommandText = @"
            TRUNCATE TABLE ""ProcessDefinitions"", ""WorkflowInstances"",
                ""WorkflowActivityInstanceEntries"", ""WorkflowVariableStates"",
                ""WorkflowConditionSequenceStates"", ""GatewayForks"", ""ComplexGatewayJoinStates"",
                ""TimerCycleTracking"", ""WorkflowSnapshots"", ""WorkflowEvents"" RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();

        var commandOptions = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansPostgres(s_dataSource!)
            .Options;

        var queryOptions = new DbContextOptionsBuilder<FleanQueryDbContext>()
            .UseFleansPostgres(s_dataSource!)
            .Options;

        _dbContextFactory = new TestDbContextFactory(commandOptions);
        _queryDbContextFactory = new TestQueryDbContextFactory(queryOptions);
        _repository = new EfCoreProcessDefinitionRepository(_dbContextFactory, _queryDbContextFactory);
    }

    [TestMethod]
    public async Task SaveAndGetById_RoundTrip_ReturnsAllScalarFields()
    {
        var deployedAt = DateTimeOffset.UtcNow;
        var definition = CreateDefinition("key1:1:ts", "key1", 1, deployedAt);

        await _repository.SaveAsync(definition);

        var result = await _repository.GetByIdAsync("key1:1:ts");

        Assert.IsNotNull(result);
        Assert.AreEqual("key1:1:ts", result.ProcessDefinitionId);
        Assert.AreEqual("key1", result.ProcessDefinitionKey);
        Assert.AreEqual(1, result.Version);
        // PostgreSQL stores timestamptz with sub-ms precision; round to seconds for comparison
        Assert.AreEqual(deployedAt.ToUnixTimeSeconds(), result.DeployedAt.ToUnixTimeSeconds());
        Assert.AreEqual("<bpmn/>", result.BpmnXml);
    }

    [TestMethod]
    public async Task SaveAndGetById_WorkflowJsonRoundTrip_PolymorphicActivityTypes()
    {
        var definition = CreateDefinitionWithMixedActivities();

        await _repository.SaveAsync(definition);

        var result = await _repository.GetByIdAsync(definition.ProcessDefinitionId);

        Assert.IsNotNull(result);
        var workflow = result.Workflow;
        Assert.AreEqual(4, workflow.Activities.Count);
        Assert.IsInstanceOfType<StartEvent>(workflow.Activities[0]);
        Assert.IsInstanceOfType<ScriptTask>(workflow.Activities[1]);
        Assert.IsInstanceOfType<ExclusiveGateway>(workflow.Activities[2]);
        Assert.IsInstanceOfType<EndEvent>(workflow.Activities[3]);

        var scriptTask = (ScriptTask)workflow.Activities[1];
        Assert.AreEqual("return 42;", scriptTask.Script);
        Assert.AreEqual("csharp", scriptTask.ScriptFormat);
    }

    [TestMethod]
    public async Task GetByKey_ReturnsVersionsOrderedByVersion()
    {
        await _repository.SaveAsync(CreateDefinition("key1:3:ts", "key1", 3, DateTimeOffset.UtcNow));
        await _repository.SaveAsync(CreateDefinition("key1:1:ts", "key1", 1, DateTimeOffset.UtcNow));
        await _repository.SaveAsync(CreateDefinition("key1:2:ts", "key1", 2, DateTimeOffset.UtcNow));

        var results = await _repository.GetByKeyAsync("key1");

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual(1, results[0].Version);
        Assert.AreEqual(2, results[1].Version);
        Assert.AreEqual(3, results[2].Version);
    }

    [TestMethod]
    public async Task GetAll_ReturnsAllDefinitionsOrderedByKeyThenVersion()
    {
        await _repository.SaveAsync(CreateDefinition("beta:2:ts", "beta", 2, DateTimeOffset.UtcNow));
        await _repository.SaveAsync(CreateDefinition("alpha:1:ts", "alpha", 1, DateTimeOffset.UtcNow));
        await _repository.SaveAsync(CreateDefinition("beta:1:ts", "beta", 1, DateTimeOffset.UtcNow));
        await _repository.SaveAsync(CreateDefinition("alpha:2:ts", "alpha", 2, DateTimeOffset.UtcNow));

        var results = await _repository.GetAllAsync();

        Assert.AreEqual(4, results.Count);
        Assert.AreEqual("alpha", results[0].ProcessDefinitionKey);
        Assert.AreEqual(1, results[0].Version);
        Assert.AreEqual("beta", results[2].ProcessDefinitionKey);
        Assert.AreEqual(1, results[2].Version);
    }

    [TestMethod]
    public async Task GetById_NonExistent_ReturnsNull()
    {
        var result = await _repository.GetByIdAsync("does-not-exist");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task Delete_RemovesDefinition_SubsequentGetByIdReturnsNull()
    {
        var definition = CreateDefinition("key1:1:ts", "key1", 1, DateTimeOffset.UtcNow);
        await _repository.SaveAsync(definition);

        await _repository.DeleteAsync("key1:1:ts");

        var result = await _repository.GetByIdAsync("key1:1:ts");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SaveAndGetById_DisabledProcess_IsActiveFalseRoundTrip()
    {
        var definition = CreateDefinition("disabled:1:ts", "disabled", 1, DateTimeOffset.UtcNow);
        definition.Disable();

        await _repository.SaveAsync(definition);

        var result = await _repository.GetByIdAsync("disabled:1:ts");

        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsActive, "IsActive should be false after round-trip");
    }

    [TestMethod]
    public async Task UpdateAsync_DisableExistingProcess_PersistsIsActiveFalse()
    {
        var definition = CreateDefinition("upd:1:ts", "upd", 1, DateTimeOffset.UtcNow);
        await _repository.SaveAsync(definition);

        definition.Disable();
        await _repository.UpdateAsync(definition);

        var result = await _repository.GetByIdAsync("upd:1:ts");
        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsActive);
    }

    [TestMethod]
    public async Task UpdateAsync_NonExistentDefinition_ThrowsInvalidOperationException()
    {
        var definition = CreateDefinition("missing:1:ts", "missing", 1, DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _repository.UpdateAsync(definition));
    }

    private static ProcessDefinition CreateDefinition(
        string id, string key, int version, DateTimeOffset deployedAt)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var flow = new SequenceFlow("flow1", start, end);

        return new ProcessDefinition
        {
            ProcessDefinitionId = id,
            ProcessDefinitionKey = key,
            Version = version,
            DeployedAt = deployedAt,
            BpmnXml = "<bpmn/>",
            Workflow = new WorkflowDefinition
            {
                WorkflowId = key,
                ProcessDefinitionId = id,
                Activities = [start, end],
                SequenceFlows = [flow]
            }
        };
    }

    private static ProcessDefinition CreateDefinitionWithMixedActivities()
    {
        var start = new StartEvent("start");
        var script = new ScriptTask("script1", "return 42;", "csharp");
        var gateway = new ExclusiveGateway("gw1");
        var end = new EndEvent("end");

        var flow1 = new SequenceFlow("flow1", start, script);
        var condFlow1 = new ConditionalSequenceFlow("condFlow1", gateway, end, "x > 10");
        var condFlow2 = new ConditionalSequenceFlow("condFlow2", gateway, script, "x <= 10");
        var defaultFlow = new DefaultSequenceFlow("defaultFlow", gateway, end);

        return new ProcessDefinition
        {
            ProcessDefinitionId = "mixed:1:ts",
            ProcessDefinitionKey = "mixed",
            Version = 1,
            DeployedAt = DateTimeOffset.UtcNow,
            BpmnXml = "<bpmn:definitions/>",
            Workflow = new WorkflowDefinition
            {
                WorkflowId = "mixed",
                ProcessDefinitionId = "mixed:1:ts",
                Activities = [start, script, gateway, end],
                SequenceFlows = [flow1, condFlow1, condFlow2, defaultFlow]
            }
        };
    }
}
