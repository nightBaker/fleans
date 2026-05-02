using System.Dynamic;
using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Fleans.Persistence;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sieve.Models;
using Sieve.Services;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class WorkflowQueryServiceTests : PersistenceTestBase
{
    private static (IWorkflowQueryService Service, IDbContextFactory<FleanCommandDbContext> CommandFactory)
        BuildService(IPersistenceTestFixture fixture)
    {
        var sieveOptions = Options.Create(new SieveOptions
        {
            DefaultPageSize = 20,
            MaxPageSize = 100
        });
        ISieveProcessor sieveProcessor = new ApplicationSieveProcessor(sieveOptions);
        var service = new WorkflowQueryService(fixture.QueryFactory, fixture.CommandFactory, sieveProcessor);
        return (service, fixture.CommandFactory);
    }

    // ─────────────────────────────────────────────────
    // GetStateSnapshot
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetStateSnapshot_ReturnsNull_WhenInstanceDoesNotExist(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        var result = await service.GetStateSnapshot(Guid.NewGuid());

        Assert.IsNull(result);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithActiveAndCompletedActivities(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        var processDefId = "proc:1:ts";
        await SeedProcessDefinition(db, processDefId, "proc", 1);
        await SeedWorkflowInstance(db, instanceId, processDefinitionId: processDefId, isStarted: true);

        var activeAiId = Guid.NewGuid();
        var completedAiId = Guid.NewGuid();

        // Active entry — set enriched fields directly on the entry
        var activeEntry = new ActivityInstanceEntry(activeAiId, "task1", instanceId);
        activeEntry.SetActivityType("TaskActivity");
        activeEntry.Execute(); // sets IsExecuting = true
        db.WorkflowActivityInstanceEntries.Add(activeEntry);
        await db.SaveChangesAsync();

        // Completed entry — set enriched fields directly on the entry
        var completedEntry = new ActivityInstanceEntry(completedAiId, "start", instanceId);
        completedEntry.SetActivityType("StartEvent");
        completedEntry.Execute();
        completedEntry.Complete(); // sets IsCompleted = true
        db.WorkflowActivityInstanceEntries.Add(completedEntry);
        await db.SaveChangesAsync();

        var result = await service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.ActiveActivityIds.Count);
        Assert.AreEqual("task1", result.ActiveActivityIds[0]);
        Assert.AreEqual(1, result.CompletedActivityIds.Count);
        Assert.AreEqual("start", result.CompletedActivityIds[0]);
        Assert.AreEqual(1, result.ActiveActivities.Count);
        Assert.AreEqual("task1", result.ActiveActivities[0].ActivityId);
        Assert.IsTrue(result.ActiveActivities[0].IsExecuting);
        Assert.AreEqual(1, result.CompletedActivities.Count);
        Assert.AreEqual("start", result.CompletedActivities[0].ActivityId);
        Assert.IsTrue(result.CompletedActivities[0].IsCompleted);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithVariables(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        var varsId = Guid.NewGuid();
        var vars = new WorkflowVariablesState(varsId, instanceId);
        db.WorkflowVariableStates.Add(vars);
        await db.SaveChangesAsync();

        // Set variables via EF property override since Merge is internal
        dynamic expando = new ExpandoObject();
        ((IDictionary<string, object>)expando)["key1"] = "value1";
        ((IDictionary<string, object>)expando)["key2"] = "42";
        db.Entry(vars).Property(e => e.Variables).CurrentValue = (ExpandoObject)expando;
        await db.SaveChangesAsync();

        var result = await service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.VariableStates.Count);
        Assert.AreEqual(varsId, result.VariableStates[0].VariablesId);
        Assert.AreEqual("value1", result.VariableStates[0].Variables["key1"]);
        Assert.AreEqual("42", result.VariableStates[0].Variables["key2"]);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithConditionSequences(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        var processDefId = "proc:1:ts";
        await SeedProcessDefinition(db, processDefId, "proc", 1, bpmnXml: "<bpmn/>",
            createConditionalFlow: true);
        await SeedWorkflowInstance(db, instanceId, processDefinitionId: processDefId, isStarted: true);

        var gatewayAiId = Guid.NewGuid();
        var cs = new ConditionSequenceState("condFlow1", gatewayAiId, instanceId);
        db.WorkflowConditionSequenceStates.Add(cs);
        db.Entry(cs).Property(e => e.Result).CurrentValue = true;
        db.Entry(cs).Property(e => e.IsEvaluated).CurrentValue = true;
        await db.SaveChangesAsync();

        var result = await service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.ConditionSequences.Count);
        Assert.AreEqual("condFlow1", result.ConditionSequences[0].SequenceFlowId);
        Assert.IsTrue(result.ConditionSequences[0].Result);
        // Condition enrichment from the workflow definition
        Assert.AreEqual("x > 10", result.ConditionSequences[0].Condition);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithProcessDefinitionId(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        var processDefId = "myproc:1:ts";
        await SeedProcessDefinition(db, processDefId, "myproc", 1);
        await SeedWorkflowInstance(db, instanceId, processDefinitionId: processDefId, isStarted: true);

        var result = await service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.AreEqual("myproc:1:ts", result.ProcessDefinitionId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithTimestamps(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var executionStartedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 1, TimeSpan.Zero);
        var completedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 5, TimeSpan.Zero);

        await SeedWorkflowInstance(db, instanceId,
            isStarted: true, isCompleted: true,
            createdAt: createdAt, executionStartedAt: executionStartedAt, completedAt: completedAt);

        var result = await service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsStarted);
        Assert.IsTrue(result.IsCompleted);
        Assert.AreEqual(createdAt, result.CreatedAt);
        Assert.AreEqual(executionStartedAt, result.ExecutionStartedAt);
        Assert.AreEqual(completedAt, result.CompletedAt);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetStateSnapshot_ReturnsErrorState_OnFailedActivity(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        var aiId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(aiId, "task1", instanceId);
        entry.SetActivityType("ScriptTask");
        entry.Execute();
        entry.Fail(new Exception("Something went wrong"));
        db.WorkflowActivityInstanceEntries.Add(entry);
        await db.SaveChangesAsync();

        var result = await service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.CompletedActivities.Count);
        var snapshot = result.CompletedActivities[0];
        Assert.IsNotNull(snapshot.ErrorState);
        Assert.AreEqual("500", snapshot.ErrorState.Code);
        Assert.AreEqual("Something went wrong", snapshot.ErrorState.Message);
    }

    // ─────────────────────────────────────────────────
    // GetAllProcessDefinitions
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetAllProcessDefinitions_ReturnsAll_OrderedByKeyThenVersion(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "beta:2:ts", "beta", 2);
        await SeedProcessDefinition(db, "alpha:1:ts", "alpha", 1);
        await SeedProcessDefinition(db, "beta:1:ts", "beta", 1);
        await SeedProcessDefinition(db, "alpha:2:ts", "alpha", 2);

        var results = await service.GetAllProcessDefinitions();

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
    public async Task GetAllProcessDefinitions_ReturnsEmpty_WhenNoneExist(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        var results = await service.GetAllProcessDefinitions();

        Assert.AreEqual(0, results.Count);
    }

    // ─────────────────────────────────────────────────
    // GetInstancesByKey
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKey_ReturnsMatchingInstances(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "mykey:1:ts", "mykey", 1);
        await SeedProcessDefinition(db, "mykey:2:ts", "mykey", 2);
        await SeedProcessDefinition(db, "other:1:ts", "other", 1);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        await SeedWorkflowInstance(db, id1, processDefinitionId: "mykey:1:ts", isStarted: true);
        await SeedWorkflowInstance(db, id2, processDefinitionId: "mykey:2:ts", isStarted: true, isCompleted: true);
        await SeedWorkflowInstance(db, id3, processDefinitionId: "other:1:ts", isStarted: true);

        var results = await service.GetInstancesByKey("mykey", new PageRequest(PageSize: 100));

        Assert.AreEqual(2, results.Items.Count);
        var instanceIds = results.Items.Select(r => r.InstanceId).ToList();
        CollectionAssert.Contains(instanceIds, id1);
        CollectionAssert.Contains(instanceIds, id2);
        CollectionAssert.DoesNotContain(instanceIds, id3);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKey_ReturnsEmpty_WhenNoMatch(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "existing:1:ts", "existing", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "existing:1:ts", isStarted: true);

        var results = await service.GetInstancesByKey("nonexistent", new PageRequest());

        Assert.AreEqual(0, results.Items.Count);
    }

    // ─────────────────────────────────────────────────
    // GetInstancesByKeyAndVersion
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKeyAndVersion_ReturnsMatchingInstances(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "key:1:ts", "key", 1);
        await SeedProcessDefinition(db, "key:2:ts", "key", 2);

        var v1Instance = Guid.NewGuid();
        var v2Instance = Guid.NewGuid();
        await SeedWorkflowInstance(db, v1Instance, processDefinitionId: "key:1:ts", isStarted: true);
        await SeedWorkflowInstance(db, v2Instance, processDefinitionId: "key:2:ts", isStarted: true);

        var results = await service.GetInstancesByKeyAndVersion("key", 1, new PageRequest());

        Assert.AreEqual(1, results.Items.Count);
        Assert.AreEqual(v1Instance, results.Items[0].InstanceId);
        Assert.AreEqual("key:1:ts", results.Items[0].ProcessDefinitionId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKeyAndVersion_ReturnsEmpty_WhenVersionNotFound(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "key:1:ts", "key", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "key:1:ts", isStarted: true);

        var results = await service.GetInstancesByKeyAndVersion("key", 99, new PageRequest());

        Assert.AreEqual(0, results.Items.Count);
    }

    // ─────────────────────────────────────────────────
    // GetInstancesByKey (paginated)
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKey_Paginated_ReturnsPagedResult(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "paged:1:ts", "paged", 1);
        for (int i = 0; i < 5; i++)
            await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "paged:1:ts", isStarted: true);

        var page1 = await service.GetInstancesByKey("paged", new PageRequest(Page: 1, PageSize: 2));

        Assert.AreEqual(2, page1.Items.Count);
        Assert.AreEqual(5, page1.TotalCount);
        Assert.AreEqual(1, page1.Page);
        Assert.AreEqual(2, page1.PageSize);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKey_Paginated_ReturnsSecondPage(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "paged2:1:ts", "paged2", 1);
        for (int i = 0; i < 5; i++)
            await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "paged2:1:ts", isStarted: true);

        var page2 = await service.GetInstancesByKey("paged2", new PageRequest(Page: 2, PageSize: 2));

        Assert.AreEqual(2, page2.Items.Count);
        Assert.AreEqual(5, page2.TotalCount);
        Assert.AreEqual(2, page2.Page);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKey_Paginated_ReturnsLastPartialPage(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "paged3:1:ts", "paged3", 1);
        for (int i = 0; i < 5; i++)
            await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "paged3:1:ts", isStarted: true);

        var page3 = await service.GetInstancesByKey("paged3", new PageRequest(Page: 3, PageSize: 2));

        Assert.AreEqual(1, page3.Items.Count);
        Assert.AreEqual(5, page3.TotalCount);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKey_Paginated_ReturnsEmpty_WhenNoMatch(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        var result = await service.GetInstancesByKey("nonexistent", new PageRequest());

        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual(0, result.TotalCount);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKey_Paginated_SortsByCreatedAtDescending(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "sorted:1:ts", "sorted", 1);
        var oldest = Guid.NewGuid();
        var newest = Guid.NewGuid();
        await SeedWorkflowInstance(db, oldest, processDefinitionId: "sorted:1:ts", isStarted: true,
            createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await SeedWorkflowInstance(db, newest, processDefinitionId: "sorted:1:ts", isStarted: true,
            createdAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var result = await service.GetInstancesByKey("sorted",
            new PageRequest(Sorts: "-CreatedAt"));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(newest, result.Items[0].InstanceId);
        Assert.AreEqual(oldest, result.Items[1].InstanceId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKey_Paginated_FiltersCompleted(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "filtered:1:ts", "filtered", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "filtered:1:ts",
            isStarted: true, isCompleted: true);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "filtered:1:ts",
            isStarted: true, isCompleted: false);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "filtered:1:ts",
            isStarted: true, isCompleted: false);

        var result = await service.GetInstancesByKey("filtered",
            new PageRequest(Filters: "IsCompleted==true"));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(1, result.TotalCount);
        Assert.IsTrue(result.Items[0].IsCompleted);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKey_Paginated_NormalizesInvalidPage(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "norm:1:ts", "norm", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "norm:1:ts", isStarted: true);

        var result = await service.GetInstancesByKey("norm",
            new PageRequest(Page: -1, PageSize: 0));

        Assert.AreEqual(1, result.Page);
        Assert.AreEqual(1, result.PageSize);
    }

    // ─────────────────────────────────────────────────
    // GetInstancesByKeyAndVersion (paginated)
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKeyAndVersion_Paginated_ReturnsPagedResult(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "pvkey:1:ts", "pvkey", 1);
        await SeedProcessDefinition(db, "pvkey:2:ts", "pvkey", 2);

        for (int i = 0; i < 3; i++)
            await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "pvkey:1:ts", isStarted: true);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "pvkey:2:ts", isStarted: true);

        var result = await service.GetInstancesByKeyAndVersion("pvkey", 1,
            new PageRequest(Page: 1, PageSize: 2));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(3, result.TotalCount);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetInstancesByKeyAndVersion_Paginated_ReturnsEmpty_WhenVersionNotFound(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "pvkey2:1:ts", "pvkey2", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "pvkey2:1:ts", isStarted: true);

        var result = await service.GetInstancesByKeyAndVersion("pvkey2", 99, new PageRequest());

        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual(0, result.TotalCount);
    }

    // ─────────────────────────────────────────────────
    // GetBpmnXml
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetBpmnXml_ReturnsBpmn_WhenInstanceExists(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var bpmn = "<bpmn:definitions>full xml here</bpmn:definitions>";
        await SeedProcessDefinition(db, "proc:1:ts", "proc", 1, bpmnXml: bpmn);

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, processDefinitionId: "proc:1:ts", isStarted: true);

        var result = await service.GetBpmnXml(instanceId);

        Assert.AreEqual(bpmn, result);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetBpmnXml_ReturnsNull_WhenInstanceNotFound(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        var result = await service.GetBpmnXml(Guid.NewGuid());

        Assert.IsNull(result);
    }

    // ─────────────────────────────────────────────────
    // GetBpmnXmlByKey
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetBpmnXmlByKey_ReturnsLatestVersion(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "proc:1:ts", "proc", 1, bpmnXml: "<bpmn>v1</bpmn>");
        await SeedProcessDefinition(db, "proc:2:ts", "proc", 2, bpmnXml: "<bpmn>v2</bpmn>");
        await SeedProcessDefinition(db, "proc:3:ts", "proc", 3, bpmnXml: "<bpmn>v3</bpmn>");

        var result = await service.GetBpmnXmlByKey("proc");

        Assert.AreEqual("<bpmn>v3</bpmn>", result);
    }

    // ─────────────────────────────────────────────────
    // GetBpmnXmlByKeyAndVersion
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetBpmnXmlByKeyAndVersion_ReturnsExactVersion(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "proc:1:ts", "proc", 1, bpmnXml: "<bpmn>v1</bpmn>");
        await SeedProcessDefinition(db, "proc:2:ts", "proc", 2, bpmnXml: "<bpmn>v2</bpmn>");

        var result = await service.GetBpmnXmlByKeyAndVersion("proc", 1);

        Assert.AreEqual("<bpmn>v1</bpmn>", result);
    }

    // ─────────────────────────────────────────────────
    // GetAllProcessDefinitions (paginated)
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetAllProcessDefinitions_Paginated_ReturnsPagedResult(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        for (int i = 1; i <= 5; i++)
            await SeedProcessDefinition(db, $"pagdef:key{i}:1:ts", $"key{i}", 1);

        var result = await service.GetAllProcessDefinitions(new PageRequest(Page: 1, PageSize: 2));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(5, result.TotalCount);
        Assert.AreEqual(1, result.Page);
        Assert.AreEqual(2, result.PageSize);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetAllProcessDefinitions_Paginated_ReturnsSecondPage(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        for (int i = 1; i <= 5; i++)
            await SeedProcessDefinition(db, $"pagdef2:key{i}:1:ts", $"pagdef2key{i}", 1);

        var result = await service.GetAllProcessDefinitions(new PageRequest(Page: 2, PageSize: 2));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(5, result.TotalCount);
        Assert.AreEqual(2, result.Page);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetAllProcessDefinitions_Paginated_FiltersByIsActive(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "active:1:ts", "activeproc", 1);
        await SeedProcessDefinition(db, "inactive:1:ts", "inactiveproc", 1);
        // Disable the second definition
        var def = await db.ProcessDefinitions.FindAsync("inactive:1:ts");
        def!.Disable();
        await db.SaveChangesAsync();

        var result = await service.GetAllProcessDefinitions(
            new PageRequest(Filters: "IsActive==true"));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual("activeproc", result.Items[0].ProcessDefinitionKey);
    }

    // ─────────────────────────────────────────────────
    // GetProcessDefinitionGroups
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetProcessDefinitionGroups_ReturnsCorrectPage(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        for (int i = 1; i <= 5; i++)
        {
            await SeedProcessDefinition(db, $"grp:key{i}:1:ts", $"grpkey{i}", 1);
            await SeedProcessDefinition(db, $"grp:key{i}:2:ts", $"grpkey{i}", 2);
        }

        var result = await service.GetProcessDefinitionGroups(
            new PageRequest(Page: 1, PageSize: 2));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(5, result.TotalCount);
        Assert.AreEqual(1, result.Page);
        Assert.AreEqual(2, result.PageSize);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetProcessDefinitionGroups_LastPage_ReturnsRemainder(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        for (int i = 1; i <= 5; i++)
            await SeedProcessDefinition(db, $"grplast:key{i}:1:ts", $"grplastkey{i}", 1);

        var result = await service.GetProcessDefinitionGroups(
            new PageRequest(Page: 3, PageSize: 2));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(5, result.TotalCount);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetProcessDefinitionGroups_SearchByKey_FiltersCorrectly(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "grpsrch:order:1:ts", "order-process", 1);
        await SeedProcessDefinition(db, "grpsrch:payment:1:ts", "payment-process", 1);
        await SeedProcessDefinition(db, "grpsrch:user:1:ts", "user-signup", 1);

        var result = await service.GetProcessDefinitionGroups(
            new PageRequest(Filters: "ProcessDefinitionKey@=order"));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual("order-process", result.Items[0].ProcessDefinitionKey);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetProcessDefinitionGroups_FilterByActive_FiltersCorrectly(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "grpact:active:1:ts", "grpactiveproc", 1);
        await SeedProcessDefinition(db, "grpact:inactive:1:ts", "grpinactiveproc", 1);
        var def = await db.ProcessDefinitions.FindAsync("grpact:inactive:1:ts");
        def!.Disable();
        await db.SaveChangesAsync();

        var result = await service.GetProcessDefinitionGroups(
            new PageRequest(Filters: "IsActive==true"));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual("grpactiveproc", result.Items[0].ProcessDefinitionKey);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetProcessDefinitionGroups_SortByDeployedAt_CorrectOrder(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "grpsort:old:1:ts", "grpsortold", 1,
            deployedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await SeedProcessDefinition(db, "grpsort:new:1:ts", "grpsortnew", 1,
            deployedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var result = await service.GetProcessDefinitionGroups(
            new PageRequest(Sorts: "-DeployedAt"));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual("grpsortnew", result.Items[0].ProcessDefinitionKey);
        Assert.AreEqual("grpsortold", result.Items[1].ProcessDefinitionKey);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetProcessDefinitionGroups_EmptyResult_ReturnsEmptyPage(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        var result = await service.GetProcessDefinitionGroups(
            new PageRequest(Filters: "ProcessDefinitionKey@=nonexistent"));

        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual(0, result.TotalCount);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetProcessDefinitionGroups_GroupContainsAllVersions(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        await SeedProcessDefinition(db, "grpver:mykey:1:ts", "grpvermykey", 1);
        await SeedProcessDefinition(db, "grpver:mykey:2:ts", "grpvermykey", 2);
        await SeedProcessDefinition(db, "grpver:mykey:3:ts", "grpvermykey", 3);

        var result = await service.GetProcessDefinitionGroups(new PageRequest());

        Assert.AreEqual(1, result.Items.Count);
        var group = result.Items[0];
        Assert.AreEqual("grpvermykey", group.ProcessDefinitionKey);
        Assert.AreEqual(3, group.Versions.Count);
        Assert.AreEqual(3, group.Versions[0].Version);
        Assert.AreEqual(2, group.Versions[1].Version);
        Assert.AreEqual(1, group.Versions[2].Version);
    }

    // ─────────────────────────────────────────────────
    // GetPendingUserTasks (paginated)
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetPendingUserTasks_Paginated_ReturnsPagedResult(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        for (int i = 0; i < 5; i++)
            await SeedUserTask(db, Guid.NewGuid(), instanceId, $"task{i}");

        var result = await service.GetPendingUserTasks(null, null,
            new PageRequest(Page: 1, PageSize: 2));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(5, result.TotalCount);
        Assert.AreEqual(1, result.Page);
        Assert.AreEqual(2, result.PageSize);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetPendingUserTasks_Paginated_FiltersByAssignee(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task1", assignee: "alice");
        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task2", assignee: "bob");
        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task3",
            candidateUsers: new List<string> { "alice", "charlie" });

        var result = await service.GetPendingUserTasks("alice", null, new PageRequest());

        // Should return task1 (direct assignment) and task3 (candidate user)
        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(2, result.TotalCount);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetPendingUserTasks_Paginated_FiltersByCandidateGroup(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task1",
            candidateGroups: new List<string> { "managers" });
        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task2",
            candidateGroups: new List<string> { "engineers" });

        var result = await service.GetPendingUserTasks(null, "managers", new PageRequest());

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(1, result.TotalCount);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetPendingUserTasks_Paginated_ExcludesCompleted(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        using var db = commandFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task1",
            taskState: UserTaskLifecycleState.Created);
        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task2",
            taskState: UserTaskLifecycleState.Completed);

        // Default Sieve filter is not set, but the existing behavior filters by
        // TaskState != Completed via Sieve filter
        var result = await service.GetPendingUserTasks(null, null,
            new PageRequest(Filters: "TaskState!=Completed"));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(1, result.TotalCount);
    }

    // ─────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────

    private static ProcessDefinition CreateProcessDefinition(
        string id, string key, int version, string bpmnXml = "<bpmn/>",
        bool createConditionalFlow = false, DateTimeOffset? deployedAt = null)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");

        List<Activity> activities;
        List<SequenceFlow> flows;

        if (createConditionalFlow)
        {
            var gateway = new ExclusiveGateway("gw1");
            var condFlow = new ConditionalSequenceFlow("condFlow1", gateway, end, "x > 10");
            var flow1 = new SequenceFlow("flow1", start, gateway);
            activities = [start, gateway, end];
            flows = [flow1, condFlow];
        }
        else
        {
            var flow = new SequenceFlow("flow1", start, end);
            activities = [start, end];
            flows = [flow];
        }

        return new ProcessDefinition
        {
            ProcessDefinitionId = id,
            ProcessDefinitionKey = key,
            Version = version,
            DeployedAt = deployedAt ?? DateTimeOffset.UtcNow,
            BpmnXml = bpmnXml,
            Workflow = new WorkflowDefinition
            {
                WorkflowId = key,
                ProcessDefinitionId = id,
                Activities = activities,
                SequenceFlows = flows
            }
        };
    }

    private static async Task SeedProcessDefinition(
        FleanCommandDbContext db, string id, string key, int version,
        string bpmnXml = "<bpmn/>", bool createConditionalFlow = false,
        DateTimeOffset? deployedAt = null)
    {
        var definition = CreateProcessDefinition(id, key, version, bpmnXml, createConditionalFlow, deployedAt);
        db.ProcessDefinitions.Add(definition);
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowInstanceState> SeedWorkflowInstance(
        FleanCommandDbContext db, Guid id, string? processDefinitionId = null,
        bool isStarted = true, bool isCompleted = false,
        DateTimeOffset? createdAt = null, DateTimeOffset? executionStartedAt = null,
        DateTimeOffset? completedAt = null)
    {
        var state = new WorkflowInstanceState();
        db.WorkflowInstances.Add(state);
        var entry = db.Entry(state);
        entry.Property(e => e.Id).CurrentValue = id;
        entry.Property(e => e.IsStarted).CurrentValue = isStarted;
        entry.Property(e => e.IsCompleted).CurrentValue = isCompleted;
        entry.Property(e => e.ProcessDefinitionId).CurrentValue = processDefinitionId;
        entry.Property(e => e.CreatedAt).CurrentValue = createdAt ?? DateTimeOffset.UtcNow;
        entry.Property(e => e.ExecutionStartedAt).CurrentValue = executionStartedAt;
        entry.Property(e => e.CompletedAt).CurrentValue = completedAt;
        await db.SaveChangesAsync();
        return state;
    }

    private static async Task SeedUserTask(
        FleanCommandDbContext db, Guid activityInstanceId, Guid workflowInstanceId,
        string activityId, string? assignee = null,
        List<string>? candidateGroups = null, List<string>? candidateUsers = null,
        UserTaskLifecycleState taskState = UserTaskLifecycleState.Created)
    {
        var task = new UserTaskState
        {
            ActivityInstanceId = activityInstanceId,
            WorkflowInstanceId = workflowInstanceId,
            ActivityId = activityId,
            Assignee = assignee,
            CandidateGroups = candidateGroups ?? [],
            CandidateUsers = candidateUsers ?? [],
            TaskState = taskState,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.UserTasks.Add(task);
        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────
    // GetRegisteredEventsAsync (#374)
    // ─────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetRegisteredEventsAsync_EmptyDb_AllListsEmpty(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, _) = BuildService(fixture);

        var snap = await service.GetRegisteredEventsAsync();

        Assert.AreEqual(0, snap.MessageStartEvents.Count);
        Assert.AreEqual(0, snap.SignalStartEvents.Count);
        Assert.AreEqual(0, snap.ConditionalStartEvents.Count);
        Assert.AreEqual(0, snap.ActiveMessageSubscriptions.Count);
        Assert.AreEqual(0, snap.ActiveSignalSubscriptions.Count);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetRegisteredEventsAsync_SeedOneOfEach_SnapshotMirrorsSeed(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        var workflowId = Guid.NewGuid();
        var hostActivityId = Guid.NewGuid();
        await using (var db = await commandFactory.CreateDbContextAsync())
        {
            db.MessageStartEventRegistrations.Add(new MessageStartEventRegistration("order-placed", "order-process"));
            db.SignalStartEventRegistrations.Add(new SignalStartEventRegistration("system-alert", "alert-process"));
            db.ConditionalStartEventListeners.Add(new ConditionalStartEventListenerState
            {
                Key = "alert-process|cond-start",
                ProcessDefinitionKey = "alert-process",
                ActivityId = "cond-start",
                ConditionExpression = "= amount > 1000",
                IsRegistered = true
            });
            db.MessageCorrelations.Add(new MessageCorrelationState { Key = "payment-received" });
            db.SignalCorrelations.Add(new SignalCorrelationState { Key = "cancel" });
            db.MessageSubscriptions.Add(new MessageSubscription(
                workflowId, "wait-payment", hostActivityId, "order-123")
            { MessageName = "payment-received" });
            db.SignalSubscriptions.Add(new SignalSubscription(
                workflowId, "wait-cancel", hostActivityId)
            { SignalName = "cancel" });
            await db.SaveChangesAsync();
        }

        var snap = await service.GetRegisteredEventsAsync();

        Assert.AreEqual(1, snap.MessageStartEvents.Count);
        Assert.AreEqual("order-placed", snap.MessageStartEvents[0].MessageName);
        Assert.AreEqual("order-process", snap.MessageStartEvents[0].ProcessDefinitionKey);

        Assert.AreEqual(1, snap.SignalStartEvents.Count);
        Assert.AreEqual("system-alert", snap.SignalStartEvents[0].SignalName);

        Assert.AreEqual(1, snap.ConditionalStartEvents.Count);
        Assert.AreEqual("= amount > 1000", snap.ConditionalStartEvents[0].ConditionExpression);

        Assert.AreEqual(1, snap.ActiveMessageSubscriptions.Count);
        var msgSub = snap.ActiveMessageSubscriptions[0];
        Assert.AreEqual("payment-received", msgSub.MessageName);
        Assert.AreEqual("order-123", msgSub.CorrelationKey);
        Assert.AreEqual(workflowId, msgSub.WorkflowInstanceId);
        Assert.AreEqual("wait-payment", msgSub.ActivityId);
        Assert.AreEqual(hostActivityId, msgSub.ActivityInstanceId);

        Assert.AreEqual(1, snap.ActiveSignalSubscriptions.Count);
        var sigSub = snap.ActiveSignalSubscriptions[0];
        Assert.AreEqual("cancel", sigSub.SignalName);
        Assert.AreEqual(workflowId, sigSub.WorkflowInstanceId);
        Assert.AreEqual(hostActivityId, sigSub.ActivityInstanceId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetRegisteredEventsAsync_ConditionalIsRegisteredFalse_Excluded(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        await using (var db = await commandFactory.CreateDbContextAsync())
        {
            db.ConditionalStartEventListeners.Add(new ConditionalStartEventListenerState
            {
                Key = "p|active",
                ProcessDefinitionKey = "p",
                ActivityId = "active",
                ConditionExpression = "= true",
                IsRegistered = true
            });
            db.ConditionalStartEventListeners.Add(new ConditionalStartEventListenerState
            {
                Key = "p|disabled",
                ProcessDefinitionKey = "p",
                ActivityId = "disabled",
                ConditionExpression = "= false",
                IsRegistered = false
            });
            await db.SaveChangesAsync();
        }

        var snap = await service.GetRegisteredEventsAsync();

        Assert.AreEqual(1, snap.ConditionalStartEvents.Count);
        Assert.AreEqual("active", snap.ConditionalStartEvents[0].ActivityId);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetRegisteredEventsAsync_MessageSubscription_DeleteThenRoundTrip(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        // Insert → snapshot includes it
        await using (var db = await commandFactory.CreateDbContextAsync())
        {
            db.MessageCorrelations.Add(new MessageCorrelationState { Key = "ephemeral" });
            db.MessageSubscriptions.Add(new MessageSubscription(
                Guid.NewGuid(), "wait", Guid.NewGuid(), "k1")
            { MessageName = "ephemeral" });
            await db.SaveChangesAsync();
        }
        var afterInsert = await service.GetRegisteredEventsAsync();
        Assert.AreEqual(1, afterInsert.ActiveMessageSubscriptions.Count);

        // Delete → snapshot excludes it (proves delete-on-completion semantic surfaces with no row-level filter)
        await using (var db = await commandFactory.CreateDbContextAsync())
        {
            var row = await db.MessageSubscriptions.FirstAsync(s => s.MessageName == "ephemeral");
            db.MessageSubscriptions.Remove(row);
            await db.SaveChangesAsync();
        }
        var afterDelete = await service.GetRegisteredEventsAsync();
        Assert.AreEqual(0, afterDelete.ActiveMessageSubscriptions.Count);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task GetRegisteredEventsAsync_SignalSubscription_DeleteThenRoundTrip(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var (service, commandFactory) = BuildService(fixture);

        var wfId = Guid.NewGuid();
        await using (var db = await commandFactory.CreateDbContextAsync())
        {
            db.SignalCorrelations.Add(new SignalCorrelationState { Key = "ephemeral-signal" });
            db.SignalSubscriptions.Add(new SignalSubscription(
                wfId, "wait", Guid.NewGuid())
            { SignalName = "ephemeral-signal" });
            await db.SaveChangesAsync();
        }
        var afterInsert = await service.GetRegisteredEventsAsync();
        Assert.AreEqual(1, afterInsert.ActiveSignalSubscriptions.Count);

        await using (var db = await commandFactory.CreateDbContextAsync())
        {
            var row = await db.SignalSubscriptions.FirstAsync(s => s.SignalName == "ephemeral-signal");
            db.SignalSubscriptions.Remove(row);
            await db.SaveChangesAsync();
        }
        var afterDelete = await service.GetRegisteredEventsAsync();
        Assert.AreEqual(0, afterDelete.ActiveSignalSubscriptions.Count);
    }
}

