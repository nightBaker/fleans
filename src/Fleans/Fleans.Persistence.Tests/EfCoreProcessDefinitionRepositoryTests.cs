using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Persistence;
using Fleans.Domain.Sequences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EfCoreProcessDefinitionRepositoryTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private IProcessDefinitionRepository _repository = null!;

    [TestInitialize]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);
        _repository = new EfCoreProcessDefinitionRepository(_dbContextFactory);

        using var db = _dbContextFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
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
        Assert.AreEqual(deployedAt, result.DeployedAt);
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
    public async Task SaveAndGetById_WorkflowJsonRoundTrip_PolymorphicSequenceFlowTypes()
    {
        var definition = CreateDefinitionWithMixedActivities();

        await _repository.SaveAsync(definition);

        var result = await _repository.GetByIdAsync(definition.ProcessDefinitionId);

        Assert.IsNotNull(result);
        var workflow = result.Workflow;

        Assert.AreEqual(4, workflow.SequenceFlows.Count);
        Assert.IsInstanceOfType<SequenceFlow>(workflow.SequenceFlows[0]);
        Assert.IsInstanceOfType<ConditionalSequenceFlow>(workflow.SequenceFlows[1]);
        Assert.IsInstanceOfType<ConditionalSequenceFlow>(workflow.SequenceFlows[2]);
        Assert.IsInstanceOfType<DefaultSequenceFlow>(workflow.SequenceFlows[3]);

        var conditionalFlow = (ConditionalSequenceFlow)workflow.SequenceFlows[1];
        Assert.AreEqual("x > 10", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task SaveAndGetById_WorkflowJsonRoundTrip_SharedActivityReferences()
    {
        var definition = CreateDefinitionWithMixedActivities();

        await _repository.SaveAsync(definition);

        var result = await _repository.GetByIdAsync(definition.ProcessDefinitionId);

        Assert.IsNotNull(result);
        var workflow = result.Workflow;

        // SequenceFlow[0].Source should be the same object as Activities[0] (StartEvent)
        Assert.IsTrue(ReferenceEquals(workflow.SequenceFlows[0].Source, workflow.Activities[0]),
            "SequenceFlow.Source should reference the same Activity instance from the Activities list");

        // SequenceFlow[0].Target should be the same object as Activities[1] (ScriptTask)
        Assert.IsTrue(ReferenceEquals(workflow.SequenceFlows[0].Target, workflow.Activities[1]),
            "SequenceFlow.Target should reference the same Activity instance from the Activities list");

        // ConditionalSequenceFlow[1].Source should be the same as Activities[2] (ExclusiveGateway)
        Assert.IsTrue(ReferenceEquals(workflow.SequenceFlows[1].Source, workflow.Activities[2]),
            "ConditionalSequenceFlow.Source should reference the same gateway Activity instance");
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
        Assert.AreEqual("alpha", results[1].ProcessDefinitionKey);
        Assert.AreEqual(2, results[1].Version);
        Assert.AreEqual("beta", results[2].ProcessDefinitionKey);
        Assert.AreEqual(1, results[2].Version);
        Assert.AreEqual("beta", results[3].ProcessDefinitionKey);
        Assert.AreEqual(2, results[3].Version);
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
    public async Task Delete_NonExistentId_IsNoOp()
    {
        await _repository.DeleteAsync("does-not-exist");

        // Should not throw — just a no-op
    }

    [TestMethod]
    public async Task Save_DuplicateProcessDefinitionId_ThrowsInvalidOperationException()
    {
        var definition = CreateDefinition("key1:1:ts", "key1", 1, DateTimeOffset.UtcNow);
        await _repository.SaveAsync(definition);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _repository.SaveAsync(definition));
    }

    // ───────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────

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
