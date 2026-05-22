using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Persistence;
using Fleans.Domain.Sequences;
using Fleans.Persistence.Tests.Infrastructure;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreProcessDefinitionRepositoryTests : PersistenceTestBase
{
    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task SaveAndGetById_RoundTrip_ReturnsAllScalarFields(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var deployedAt = DateTimeOffset.UtcNow;
        var definition = CreateDefinition("key1:1:ts", "key1", 1, deployedAt);

        await repository.SaveAsync(definition);

        var result = await repository.GetByIdAsync("key1:1:ts");

        Assert.IsNotNull(result);
        Assert.AreEqual("key1:1:ts", result.ProcessDefinitionId);
        Assert.AreEqual("key1", result.ProcessDefinitionKey);
        Assert.AreEqual(1, result.Version);
        Assert.AreEqual(TruncateToMicroseconds(deployedAt), TruncateToMicroseconds(result.DeployedAt));
        Assert.AreEqual("<bpmn/>", result.BpmnXml);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task SaveAndGetById_WorkflowJsonRoundTrip_PolymorphicActivityTypes(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinitionWithMixedActivities();

        await repository.SaveAsync(definition);

        var result = await repository.GetByIdAsync(definition.ProcessDefinitionId);

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

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task SaveAndGetById_WorkflowJsonRoundTrip_PolymorphicSequenceFlowTypes(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinitionWithMixedActivities();

        await repository.SaveAsync(definition);

        var result = await repository.GetByIdAsync(definition.ProcessDefinitionId);

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

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task SaveAndGetById_WorkflowJsonRoundTrip_SharedActivityReferences(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinitionWithMixedActivities();

        await repository.SaveAsync(definition);

        var result = await repository.GetByIdAsync(definition.ProcessDefinitionId);

        Assert.IsNotNull(result);
        var workflow = result.Workflow;

        Assert.IsTrue(ReferenceEquals(workflow.SequenceFlows[0].Source, workflow.Activities[0]),
            "SequenceFlow.Source should reference the same Activity instance from the Activities list");

        Assert.IsTrue(ReferenceEquals(workflow.SequenceFlows[0].Target, workflow.Activities[1]),
            "SequenceFlow.Target should reference the same Activity instance from the Activities list");

        Assert.IsTrue(ReferenceEquals(workflow.SequenceFlows[1].Source, workflow.Activities[2]),
            "ConditionalSequenceFlow.Source should reference the same gateway Activity instance");
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetByKey_ReturnsVersionsOrderedByVersion(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        await repository.SaveAsync(CreateDefinition("key1:3:ts", "key1", 3, DateTimeOffset.UtcNow));
        await repository.SaveAsync(CreateDefinition("key1:1:ts", "key1", 1, DateTimeOffset.UtcNow));
        await repository.SaveAsync(CreateDefinition("key1:2:ts", "key1", 2, DateTimeOffset.UtcNow));

        var results = await repository.GetByKeyAsync("key1");

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual(1, results[0].Version);
        Assert.AreEqual(2, results[1].Version);
        Assert.AreEqual(3, results[2].Version);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetAll_ReturnsAllDefinitionsOrderedByKeyThenVersion(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        await repository.SaveAsync(CreateDefinition("beta:2:ts", "beta", 2, DateTimeOffset.UtcNow));
        await repository.SaveAsync(CreateDefinition("alpha:1:ts", "alpha", 1, DateTimeOffset.UtcNow));
        await repository.SaveAsync(CreateDefinition("beta:1:ts", "beta", 1, DateTimeOffset.UtcNow));
        await repository.SaveAsync(CreateDefinition("alpha:2:ts", "alpha", 2, DateTimeOffset.UtcNow));

        var results = await repository.GetAllAsync();

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

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetById_NonExistent_ReturnsNull(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var result = await repository.GetByIdAsync("does-not-exist");

        Assert.IsNull(result);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Delete_RemovesDefinition_SubsequentGetByIdReturnsNull(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinition("key1:1:ts", "key1", 1, DateTimeOffset.UtcNow);
        await repository.SaveAsync(definition);

        await repository.DeleteAsync("key1:1:ts");

        var result = await repository.GetByIdAsync("key1:1:ts");
        Assert.IsNull(result);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Delete_NonExistentId_IsNoOp(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        await repository.DeleteAsync("does-not-exist");
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Save_DuplicateProcessDefinitionId_ThrowsInvalidOperationException(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinition("key1:1:ts", "key1", 1, DateTimeOffset.UtcNow);
        await repository.SaveAsync(definition);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.SaveAsync(definition));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task SaveAndGetById_DisabledProcess_IsActiveFalseRoundTrip(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinition("disabled:1:ts", "disabled", 1, DateTimeOffset.UtcNow);
        definition.Disable();

        await repository.SaveAsync(definition);

        var result = await repository.GetByIdAsync("disabled:1:ts");

        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsActive, "IsActive should be false after round-trip");
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task SaveAndGetById_EnabledProcess_IsActiveTrueByDefault(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinition("enabled:1:ts", "enabled", 1, DateTimeOffset.UtcNow);

        await repository.SaveAsync(definition);

        var result = await repository.GetByIdAsync("enabled:1:ts");

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsActive, "IsActive should be true by default");
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task UpdateAsync_DisableExistingProcess_PersistsIsActiveFalse(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinition("upd:1:ts", "upd", 1, DateTimeOffset.UtcNow);
        await repository.SaveAsync(definition);

        definition.Disable();
        await repository.UpdateAsync(definition);

        var result = await repository.GetByIdAsync("upd:1:ts");
        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsActive, "IsActive should be false after UpdateAsync with Disable()");
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task UpdateAsync_EnableDisabledProcess_PersistsIsActiveTrue(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinition("upd2:1:ts", "upd2", 1, DateTimeOffset.UtcNow);
        definition.Disable();
        await repository.SaveAsync(definition);

        definition.Enable();
        await repository.UpdateAsync(definition);

        var result = await repository.GetByIdAsync("upd2:1:ts");
        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsActive, "IsActive should be true after UpdateAsync with Enable()");
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task UpdateAsync_NonExistentDefinition_ThrowsInvalidOperationException(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var repository = new EfCoreProcessDefinitionRepository(fixture.CommandFactory, fixture.QueryContextFactory);

        var definition = CreateDefinition("missing:1:ts", "missing", 1, DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.UpdateAsync(definition));
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => new(value.Ticks - value.Ticks % 10, value.Offset);

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
