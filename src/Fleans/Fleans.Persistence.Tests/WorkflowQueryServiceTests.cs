using System.Dynamic;
using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Fleans.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sieve.Models;
using Sieve.Services;

namespace Fleans.Persistence.Tests;

[TestClass]
public class WorkflowQueryServiceTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanCommandDbContext> _commandDbContextFactory = null!;
    private IDbContextFactory<FleanQueryDbContext> _queryDbContextFactory = null!;
    private IWorkflowQueryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var commandOptions = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseSqlite(_connection)
            .Options;

        var queryOptions = new DbContextOptionsBuilder<FleanQueryDbContext>()
            .UseSqlite(_connection)
            .Options;

        _commandDbContextFactory = new TestCommandDbContextFactory(commandOptions);
        _queryDbContextFactory = new TestQueryDbContextFactory(queryOptions);
        var sieveOptions = Options.Create(new SieveOptions
        {
            DefaultPageSize = 20,
            MaxPageSize = 100
        });
        ISieveProcessor sieveProcessor = new ApplicationSieveProcessor(sieveOptions);
        _service = new WorkflowQueryService(_queryDbContextFactory, sieveProcessor);

        using var db = _commandDbContextFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
    }

    // ─────────────────────────────────────────────────
    // GetStateSnapshot
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetStateSnapshot_ReturnsNull_WhenInstanceDoesNotExist()
    {
        var result = await _service.GetStateSnapshot(Guid.NewGuid());

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithActiveAndCompletedActivities()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

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

        var result = await _service.GetStateSnapshot(instanceId);

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

    [TestMethod]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithVariables()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

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

        var result = await _service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.VariableStates.Count);
        Assert.AreEqual(varsId, result.VariableStates[0].VariablesId);
        Assert.AreEqual("value1", result.VariableStates[0].Variables["key1"]);
        Assert.AreEqual("42", result.VariableStates[0].Variables["key2"]);
    }

    [TestMethod]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithConditionSequences()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

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

        var result = await _service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.ConditionSequences.Count);
        Assert.AreEqual("condFlow1", result.ConditionSequences[0].SequenceFlowId);
        Assert.IsTrue(result.ConditionSequences[0].Result);
        // Condition enrichment from the workflow definition
        Assert.AreEqual("x > 10", result.ConditionSequences[0].Condition);
    }

    [TestMethod]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithProcessDefinitionId()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        var processDefId = "myproc:1:ts";
        await SeedProcessDefinition(db, processDefId, "myproc", 1);
        await SeedWorkflowInstance(db, instanceId, processDefinitionId: processDefId, isStarted: true);

        var result = await _service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.AreEqual("myproc:1:ts", result.ProcessDefinitionId);
    }

    [TestMethod]
    public async Task GetStateSnapshot_ReturnsSnapshot_WithTimestamps()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var executionStartedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 1, TimeSpan.Zero);
        var completedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 5, TimeSpan.Zero);

        await SeedWorkflowInstance(db, instanceId,
            isStarted: true, isCompleted: true,
            createdAt: createdAt, executionStartedAt: executionStartedAt, completedAt: completedAt);

        var result = await _service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsStarted);
        Assert.IsTrue(result.IsCompleted);
        Assert.AreEqual(createdAt, result.CreatedAt);
        Assert.AreEqual(executionStartedAt, result.ExecutionStartedAt);
        Assert.AreEqual(completedAt, result.CompletedAt);
    }

    [TestMethod]
    public async Task GetStateSnapshot_ReturnsErrorState_OnFailedActivity()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        var aiId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(aiId, "task1", instanceId);
        entry.SetActivityType("ScriptTask");
        entry.Execute();
        entry.Fail(new Exception("Something went wrong"));
        db.WorkflowActivityInstanceEntries.Add(entry);
        await db.SaveChangesAsync();

        var result = await _service.GetStateSnapshot(instanceId);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.CompletedActivities.Count);
        var snapshot = result.CompletedActivities[0];
        Assert.IsNotNull(snapshot.ErrorState);
        Assert.AreEqual(500, snapshot.ErrorState.Code);
        Assert.AreEqual("Something went wrong", snapshot.ErrorState.Message);
    }

    // ─────────────────────────────────────────────────
    // GetAllProcessDefinitions
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetAllProcessDefinitions_ReturnsAll_OrderedByKeyThenVersion()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "beta:2:ts", "beta", 2);
        await SeedProcessDefinition(db, "alpha:1:ts", "alpha", 1);
        await SeedProcessDefinition(db, "beta:1:ts", "beta", 1);
        await SeedProcessDefinition(db, "alpha:2:ts", "alpha", 2);

        var results = await _service.GetAllProcessDefinitions();

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
    public async Task GetAllProcessDefinitions_ReturnsEmpty_WhenNoneExist()
    {
        var results = await _service.GetAllProcessDefinitions();

        Assert.AreEqual(0, results.Count);
    }

    // ─────────────────────────────────────────────────
    // GetInstancesByKey
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetInstancesByKey_ReturnsMatchingInstances()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "mykey:1:ts", "mykey", 1);
        await SeedProcessDefinition(db, "mykey:2:ts", "mykey", 2);
        await SeedProcessDefinition(db, "other:1:ts", "other", 1);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        await SeedWorkflowInstance(db, id1, processDefinitionId: "mykey:1:ts", isStarted: true);
        await SeedWorkflowInstance(db, id2, processDefinitionId: "mykey:2:ts", isStarted: true, isCompleted: true);
        await SeedWorkflowInstance(db, id3, processDefinitionId: "other:1:ts", isStarted: true);

        var results = await _service.GetInstancesByKey("mykey", new PageRequest(PageSize: 100));

        Assert.AreEqual(2, results.Items.Count);
        var instanceIds = results.Items.Select(r => r.InstanceId).ToList();
        CollectionAssert.Contains(instanceIds, id1);
        CollectionAssert.Contains(instanceIds, id2);
        CollectionAssert.DoesNotContain(instanceIds, id3);
    }

    [TestMethod]
    public async Task GetInstancesByKey_ReturnsEmpty_WhenNoMatch()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "existing:1:ts", "existing", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "existing:1:ts", isStarted: true);

        var results = await _service.GetInstancesByKey("nonexistent", new PageRequest());

        Assert.AreEqual(0, results.Items.Count);
    }

    // ─────────────────────────────────────────────────
    // GetInstancesByKeyAndVersion
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetInstancesByKeyAndVersion_ReturnsMatchingInstances()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "key:1:ts", "key", 1);
        await SeedProcessDefinition(db, "key:2:ts", "key", 2);

        var v1Instance = Guid.NewGuid();
        var v2Instance = Guid.NewGuid();
        await SeedWorkflowInstance(db, v1Instance, processDefinitionId: "key:1:ts", isStarted: true);
        await SeedWorkflowInstance(db, v2Instance, processDefinitionId: "key:2:ts", isStarted: true);

        var results = await _service.GetInstancesByKeyAndVersion("key", 1, new PageRequest());

        Assert.AreEqual(1, results.Items.Count);
        Assert.AreEqual(v1Instance, results.Items[0].InstanceId);
        Assert.AreEqual("key:1:ts", results.Items[0].ProcessDefinitionId);
    }

    [TestMethod]
    public async Task GetInstancesByKeyAndVersion_ReturnsEmpty_WhenVersionNotFound()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "key:1:ts", "key", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "key:1:ts", isStarted: true);

        var results = await _service.GetInstancesByKeyAndVersion("key", 99, new PageRequest());

        Assert.AreEqual(0, results.Items.Count);
    }

    // ─────────────────────────────────────────────────
    // GetInstancesByKey (paginated)
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetInstancesByKey_Paginated_ReturnsPagedResult()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "paged:1:ts", "paged", 1);
        for (int i = 0; i < 5; i++)
            await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "paged:1:ts", isStarted: true);

        var page1 = await _service.GetInstancesByKey("paged", new PageRequest(Page: 1, PageSize: 2));

        Assert.AreEqual(2, page1.Items.Count);
        Assert.AreEqual(5, page1.TotalCount);
        Assert.AreEqual(1, page1.Page);
        Assert.AreEqual(2, page1.PageSize);
    }

    [TestMethod]
    public async Task GetInstancesByKey_Paginated_ReturnsSecondPage()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "paged2:1:ts", "paged2", 1);
        for (int i = 0; i < 5; i++)
            await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "paged2:1:ts", isStarted: true);

        var page2 = await _service.GetInstancesByKey("paged2", new PageRequest(Page: 2, PageSize: 2));

        Assert.AreEqual(2, page2.Items.Count);
        Assert.AreEqual(5, page2.TotalCount);
        Assert.AreEqual(2, page2.Page);
    }

    [TestMethod]
    public async Task GetInstancesByKey_Paginated_ReturnsLastPartialPage()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "paged3:1:ts", "paged3", 1);
        for (int i = 0; i < 5; i++)
            await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "paged3:1:ts", isStarted: true);

        var page3 = await _service.GetInstancesByKey("paged3", new PageRequest(Page: 3, PageSize: 2));

        Assert.AreEqual(1, page3.Items.Count);
        Assert.AreEqual(5, page3.TotalCount);
    }

    [TestMethod]
    public async Task GetInstancesByKey_Paginated_ReturnsEmpty_WhenNoMatch()
    {
        var result = await _service.GetInstancesByKey("nonexistent", new PageRequest());

        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual(0, result.TotalCount);
    }

    [TestMethod]
    public async Task GetInstancesByKey_Paginated_SortsByCreatedAtDescending()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "sorted:1:ts", "sorted", 1);
        var oldest = Guid.NewGuid();
        var newest = Guid.NewGuid();
        await SeedWorkflowInstance(db, oldest, processDefinitionId: "sorted:1:ts", isStarted: true,
            createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await SeedWorkflowInstance(db, newest, processDefinitionId: "sorted:1:ts", isStarted: true,
            createdAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var result = await _service.GetInstancesByKey("sorted",
            new PageRequest(Sorts: "-CreatedAt"));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(newest, result.Items[0].InstanceId);
        Assert.AreEqual(oldest, result.Items[1].InstanceId);
    }

    [TestMethod]
    public async Task GetInstancesByKey_Paginated_FiltersCompleted()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "filtered:1:ts", "filtered", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "filtered:1:ts",
            isStarted: true, isCompleted: true);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "filtered:1:ts",
            isStarted: true, isCompleted: false);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "filtered:1:ts",
            isStarted: true, isCompleted: false);

        var result = await _service.GetInstancesByKey("filtered",
            new PageRequest(Filters: "IsCompleted==true"));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(1, result.TotalCount);
        Assert.IsTrue(result.Items[0].IsCompleted);
    }

    [TestMethod]
    public async Task GetInstancesByKey_Paginated_NormalizesInvalidPage()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "norm:1:ts", "norm", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "norm:1:ts", isStarted: true);

        var result = await _service.GetInstancesByKey("norm",
            new PageRequest(Page: -1, PageSize: 0));

        Assert.AreEqual(1, result.Page);
        Assert.AreEqual(1, result.PageSize);
    }

    // ─────────────────────────────────────────────────
    // GetInstancesByKeyAndVersion (paginated)
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetInstancesByKeyAndVersion_Paginated_ReturnsPagedResult()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "pvkey:1:ts", "pvkey", 1);
        await SeedProcessDefinition(db, "pvkey:2:ts", "pvkey", 2);

        for (int i = 0; i < 3; i++)
            await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "pvkey:1:ts", isStarted: true);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "pvkey:2:ts", isStarted: true);

        var result = await _service.GetInstancesByKeyAndVersion("pvkey", 1,
            new PageRequest(Page: 1, PageSize: 2));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(3, result.TotalCount);
    }

    [TestMethod]
    public async Task GetInstancesByKeyAndVersion_Paginated_ReturnsEmpty_WhenVersionNotFound()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "pvkey2:1:ts", "pvkey2", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "pvkey2:1:ts", isStarted: true);

        var result = await _service.GetInstancesByKeyAndVersion("pvkey2", 99, new PageRequest());

        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual(0, result.TotalCount);
    }

    // ─────────────────────────────────────────────────
    // GetBpmnXml
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetBpmnXml_ReturnsBpmn_WhenInstanceExists()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        var bpmn = "<bpmn:definitions>full xml here</bpmn:definitions>";
        await SeedProcessDefinition(db, "proc:1:ts", "proc", 1, bpmnXml: bpmn);

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, processDefinitionId: "proc:1:ts", isStarted: true);

        var result = await _service.GetBpmnXml(instanceId);

        Assert.AreEqual(bpmn, result);
    }

    [TestMethod]
    public async Task GetBpmnXml_ReturnsNull_WhenInstanceNotFound()
    {
        var result = await _service.GetBpmnXml(Guid.NewGuid());

        Assert.IsNull(result);
    }

    // ─────────────────────────────────────────────────
    // GetBpmnXmlByKey
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetBpmnXmlByKey_ReturnsLatestVersion()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "proc:1:ts", "proc", 1, bpmnXml: "<bpmn>v1</bpmn>");
        await SeedProcessDefinition(db, "proc:2:ts", "proc", 2, bpmnXml: "<bpmn>v2</bpmn>");
        await SeedProcessDefinition(db, "proc:3:ts", "proc", 3, bpmnXml: "<bpmn>v3</bpmn>");

        var result = await _service.GetBpmnXmlByKey("proc");

        Assert.AreEqual("<bpmn>v3</bpmn>", result);
    }

    // ─────────────────────────────────────────────────
    // GetBpmnXmlByKeyAndVersion
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetBpmnXmlByKeyAndVersion_ReturnsExactVersion()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "proc:1:ts", "proc", 1, bpmnXml: "<bpmn>v1</bpmn>");
        await SeedProcessDefinition(db, "proc:2:ts", "proc", 2, bpmnXml: "<bpmn>v2</bpmn>");

        var result = await _service.GetBpmnXmlByKeyAndVersion("proc", 1);

        Assert.AreEqual("<bpmn>v1</bpmn>", result);
    }

    // ─────────────────────────────────────────────────
    // GetAllProcessDefinitions (paginated)
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetAllProcessDefinitions_Paginated_ReturnsPagedResult()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        for (int i = 1; i <= 5; i++)
            await SeedProcessDefinition(db, $"pagdef:key{i}:1:ts", $"key{i}", 1);

        var result = await _service.GetAllProcessDefinitions(new PageRequest(Page: 1, PageSize: 2));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(5, result.TotalCount);
        Assert.AreEqual(1, result.Page);
        Assert.AreEqual(2, result.PageSize);
    }

    [TestMethod]
    public async Task GetAllProcessDefinitions_Paginated_ReturnsSecondPage()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        for (int i = 1; i <= 5; i++)
            await SeedProcessDefinition(db, $"pagdef2:key{i}:1:ts", $"pagdef2key{i}", 1);

        var result = await _service.GetAllProcessDefinitions(new PageRequest(Page: 2, PageSize: 2));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(5, result.TotalCount);
        Assert.AreEqual(2, result.Page);
    }

    [TestMethod]
    public async Task GetAllProcessDefinitions_Paginated_FiltersByIsActive()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "active:1:ts", "activeproc", 1);
        await SeedProcessDefinition(db, "inactive:1:ts", "inactiveproc", 1);
        // Disable the second definition
        var def = await db.ProcessDefinitions.FindAsync("inactive:1:ts");
        def!.Disable();
        await db.SaveChangesAsync();

        var result = await _service.GetAllProcessDefinitions(
            new PageRequest(Filters: "IsActive==true"));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual("activeproc", result.Items[0].ProcessDefinitionKey);
    }

    // ─────────────────────────────────────────────────
    // GetPendingUserTasks (paginated)
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetPendingUserTasks_Paginated_ReturnsPagedResult()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        for (int i = 0; i < 5; i++)
            await SeedUserTask(db, Guid.NewGuid(), instanceId, $"task{i}");

        var result = await _service.GetPendingUserTasks(null, null,
            new PageRequest(Page: 1, PageSize: 2));

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(5, result.TotalCount);
        Assert.AreEqual(1, result.Page);
        Assert.AreEqual(2, result.PageSize);
    }

    [TestMethod]
    public async Task GetPendingUserTasks_Paginated_FiltersByAssignee()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task1", assignee: "alice");
        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task2", assignee: "bob");
        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task3",
            candidateUsers: new List<string> { "alice", "charlie" });

        var result = await _service.GetPendingUserTasks("alice", null, new PageRequest());

        // Should return task1 (direct assignment) and task3 (candidate user)
        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(2, result.TotalCount);
    }

    [TestMethod]
    public async Task GetPendingUserTasks_Paginated_FiltersByCandidateGroup()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task1",
            candidateGroups: new List<string> { "managers" });
        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task2",
            candidateGroups: new List<string> { "engineers" });

        var result = await _service.GetPendingUserTasks(null, "managers", new PageRequest());

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(1, result.TotalCount);
    }

    [TestMethod]
    public async Task GetPendingUserTasks_Paginated_ExcludesCompleted()
    {
        using var db = _commandDbContextFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task1",
            taskState: UserTaskLifecycleState.Created);
        await SeedUserTask(db, Guid.NewGuid(), instanceId, "task2",
            taskState: UserTaskLifecycleState.Completed);

        // Default Sieve filter is not set, but the existing behavior filters by
        // TaskState != Completed via Sieve filter
        var result = await _service.GetPendingUserTasks(null, null,
            new PageRequest(Filters: "TaskState!=Completed"));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(1, result.TotalCount);
    }

    // ─────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────

    private static ProcessDefinition CreateProcessDefinition(
        string id, string key, int version, string bpmnXml = "<bpmn/>",
        bool createConditionalFlow = false)
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
            DeployedAt = DateTimeOffset.UtcNow,
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
        string bpmnXml = "<bpmn/>", bool createConditionalFlow = false)
    {
        var definition = CreateProcessDefinition(id, key, version, bpmnXml, createConditionalFlow);
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

    private class TestCommandDbContextFactory : IDbContextFactory<FleanCommandDbContext>
    {
        private readonly DbContextOptions<FleanCommandDbContext> _options;

        public TestCommandDbContextFactory(DbContextOptions<FleanCommandDbContext> options)
        {
            _options = options;
        }

        public FleanCommandDbContext CreateDbContext() => new(_options);

        public Task<FleanCommandDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private class TestQueryDbContextFactory : IDbContextFactory<FleanQueryDbContext>
    {
        private readonly DbContextOptions<FleanQueryDbContext> _options;

        public TestQueryDbContextFactory(DbContextOptions<FleanQueryDbContext> options)
        {
            _options = options;
        }

        public FleanQueryDbContext CreateDbContext() => new(_options);

        public Task<FleanQueryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
