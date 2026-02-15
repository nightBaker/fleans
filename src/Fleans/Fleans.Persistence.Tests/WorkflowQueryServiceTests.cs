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

namespace Fleans.Persistence.Tests;

[TestClass]
public class WorkflowQueryServiceTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanDbContext> _dbContextFactory = null!;
    private IWorkflowQueryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<FleanDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);
        _service = new WorkflowQueryService(_dbContextFactory);

        using var db = _dbContextFactory.CreateDbContext();
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
        using var db = _dbContextFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        var processDefId = "proc:1:ts";
        await SeedProcessDefinition(db, processDefId, "proc", 1);
        await SeedWorkflowInstance(db, instanceId, processDefinitionId: processDefId, isStarted: true);

        var activeAiId = Guid.NewGuid();
        var completedAiId = Guid.NewGuid();

        // Active entry + activity instance state
        var activeEntry = new ActivityInstanceEntry(activeAiId, "task1", instanceId);
        db.WorkflowActivityInstanceEntries.Add(activeEntry);
        await db.SaveChangesAsync();

        await SeedActivityInstance(db, activeAiId, "task1", "TaskActivity", isExecuting: true);

        // Completed entry + activity instance state
        var completedEntry = new ActivityInstanceEntry(completedAiId, "start", instanceId);
        db.WorkflowActivityInstanceEntries.Add(completedEntry);
        db.Entry(completedEntry).Property(e => e.IsCompleted).CurrentValue = true;
        await db.SaveChangesAsync();

        await SeedActivityInstance(db, completedAiId, "start", "StartEvent", isCompleted: true);

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
        using var db = _dbContextFactory.CreateDbContext();

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
        using var db = _dbContextFactory.CreateDbContext();

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
        using var db = _dbContextFactory.CreateDbContext();

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
        using var db = _dbContextFactory.CreateDbContext();

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
        using var db = _dbContextFactory.CreateDbContext();

        var instanceId = Guid.NewGuid();
        await SeedWorkflowInstance(db, instanceId, isStarted: true);

        var aiId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(aiId, "task1", instanceId);
        db.WorkflowActivityInstanceEntries.Add(entry);
        db.Entry(entry).Property(e => e.IsCompleted).CurrentValue = true;
        await db.SaveChangesAsync();

        await SeedActivityInstance(db, aiId, "task1", "ScriptTask",
            isCompleted: true, errorCode: 500, errorMessage: "Something went wrong");

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
        using var db = _dbContextFactory.CreateDbContext();

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
        using var db = _dbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "mykey:1:ts", "mykey", 1);
        await SeedProcessDefinition(db, "mykey:2:ts", "mykey", 2);
        await SeedProcessDefinition(db, "other:1:ts", "other", 1);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        await SeedWorkflowInstance(db, id1, processDefinitionId: "mykey:1:ts", isStarted: true);
        await SeedWorkflowInstance(db, id2, processDefinitionId: "mykey:2:ts", isStarted: true, isCompleted: true);
        await SeedWorkflowInstance(db, id3, processDefinitionId: "other:1:ts", isStarted: true);

        var results = await _service.GetInstancesByKey("mykey");

        Assert.AreEqual(2, results.Count);
        var instanceIds = results.Select(r => r.InstanceId).ToList();
        CollectionAssert.Contains(instanceIds, id1);
        CollectionAssert.Contains(instanceIds, id2);
        CollectionAssert.DoesNotContain(instanceIds, id3);
    }

    [TestMethod]
    public async Task GetInstancesByKey_ReturnsEmpty_WhenNoMatch()
    {
        using var db = _dbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "existing:1:ts", "existing", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "existing:1:ts", isStarted: true);

        var results = await _service.GetInstancesByKey("nonexistent");

        Assert.AreEqual(0, results.Count);
    }

    // ─────────────────────────────────────────────────
    // GetInstancesByKeyAndVersion
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetInstancesByKeyAndVersion_ReturnsMatchingInstances()
    {
        using var db = _dbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "key:1:ts", "key", 1);
        await SeedProcessDefinition(db, "key:2:ts", "key", 2);

        var v1Instance = Guid.NewGuid();
        var v2Instance = Guid.NewGuid();
        await SeedWorkflowInstance(db, v1Instance, processDefinitionId: "key:1:ts", isStarted: true);
        await SeedWorkflowInstance(db, v2Instance, processDefinitionId: "key:2:ts", isStarted: true);

        var results = await _service.GetInstancesByKeyAndVersion("key", 1);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(v1Instance, results[0].InstanceId);
        Assert.AreEqual("key:1:ts", results[0].ProcessDefinitionId);
    }

    [TestMethod]
    public async Task GetInstancesByKeyAndVersion_ReturnsEmpty_WhenVersionNotFound()
    {
        using var db = _dbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "key:1:ts", "key", 1);
        await SeedWorkflowInstance(db, Guid.NewGuid(), processDefinitionId: "key:1:ts", isStarted: true);

        var results = await _service.GetInstancesByKeyAndVersion("key", 99);

        Assert.AreEqual(0, results.Count);
    }

    // ─────────────────────────────────────────────────
    // GetBpmnXml
    // ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetBpmnXml_ReturnsBpmn_WhenInstanceExists()
    {
        using var db = _dbContextFactory.CreateDbContext();

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
        using var db = _dbContextFactory.CreateDbContext();

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
        using var db = _dbContextFactory.CreateDbContext();

        await SeedProcessDefinition(db, "proc:1:ts", "proc", 1, bpmnXml: "<bpmn>v1</bpmn>");
        await SeedProcessDefinition(db, "proc:2:ts", "proc", 2, bpmnXml: "<bpmn>v2</bpmn>");

        var result = await _service.GetBpmnXmlByKeyAndVersion("proc", 1);

        Assert.AreEqual("<bpmn>v1</bpmn>", result);
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
        FleanDbContext db, string id, string key, int version,
        string bpmnXml = "<bpmn/>", bool createConditionalFlow = false)
    {
        var definition = CreateProcessDefinition(id, key, version, bpmnXml, createConditionalFlow);
        db.ProcessDefinitions.Add(definition);
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowInstanceState> SeedWorkflowInstance(
        FleanDbContext db, Guid id, string? processDefinitionId = null,
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

    private static async Task SeedActivityInstance(
        FleanDbContext db, Guid id, string activityId, string activityType,
        bool isCompleted = false, bool isExecuting = false,
        int? errorCode = null, string? errorMessage = null,
        DateTimeOffset? createdAt = null, DateTimeOffset? executionStartedAt = null,
        DateTimeOffset? completedAt = null)
    {
        var state = new ActivityInstanceState();
        db.ActivityInstances.Add(state);
        var entry = db.Entry(state);
        entry.Property(e => e.Id).CurrentValue = id;
        entry.Property(e => e.ActivityId).CurrentValue = activityId;
        entry.Property(e => e.ActivityType).CurrentValue = activityType;
        entry.Property(e => e.IsCompleted).CurrentValue = isCompleted;
        entry.Property(e => e.IsExecuting).CurrentValue = isExecuting;
        entry.Property(e => e.ErrorCode).CurrentValue = errorCode;
        entry.Property(e => e.ErrorMessage).CurrentValue = errorMessage;
        entry.Property(e => e.CreatedAt).CurrentValue = createdAt ?? DateTimeOffset.UtcNow;
        entry.Property(e => e.ExecutionStartedAt).CurrentValue = executionStartedAt;
        entry.Property(e => e.CompletedAt).CurrentValue = completedAt;
        await db.SaveChangesAsync();
    }

    private class TestDbContextFactory : IDbContextFactory<FleanDbContext>
    {
        private readonly DbContextOptions<FleanDbContext> _options;

        public TestDbContextFactory(DbContextOptions<FleanDbContext> options)
        {
            _options = options;
        }

        public FleanDbContext CreateDbContext() => new(_options);

        public Task<FleanDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
