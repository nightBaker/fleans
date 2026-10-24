using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// #824 — business id on process instances.
/// </summary>
[TestClass]
public class BusinessIdTests : WorkflowTestBase
{
    private IWorkflowCommandService CommandService => GetSiloService<IWorkflowCommandService>();

    [TestMethod]
    public async Task StartWorkflow_WithBusinessId_ExposesItOnSnapshotGrainAndInstanceQuery()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-simple"));

        // Act
        var instanceId = await CommandService.StartWorkflow("biz-simple", businessId: "ORDER-1");

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual("ORDER-1", snapshot.BusinessId);

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        Assert.AreEqual("ORDER-1", await grain.GetBusinessId());

        await WaitForProjection(instanceId);
        var filtered = await QueryService.GetInstancesByKey(
            "biz-simple", new PageRequest(Filters: "BusinessId==ORDER-1"));
        Assert.HasCount(1, filtered.Items);
        Assert.AreEqual(instanceId, filtered.Items[0].InstanceId);
        Assert.AreEqual("ORDER-1", filtered.Items[0].BusinessId);
    }

    [TestMethod]
    public async Task StartWorkflow_WithoutBusinessId_LeavesItNull()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-none"));

        // Act
        var instanceId = await CommandService.StartWorkflow("biz-none");

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsNull(snapshot.BusinessId);
    }

    [TestMethod]
    public async Task BusinessId_SurvivesGrainReactivation()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-replay"));
        var instanceId = await CommandService.StartWorkflow("biz-replay", businessId: "ORDER-REPLAY");

        // Act — force replay from the event store
        await ForceAllGrainDeactivation();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);

        // Assert
        Assert.AreEqual("ORDER-REPLAY", await grain.GetBusinessId());
    }

    [TestMethod]
    public async Task BusinessId_StaysAfterCompletion()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-complete"));
        var instanceId = await CommandService.StartWorkflow("biz-complete", businessId: "ORDER-DONE");

        // Act
        await CommandService.CompleteActivity(instanceId, "task", new ExpandoObject());

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.AreEqual("ORDER-DONE", snapshot.BusinessId);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task StartWorkflow_WithBlankBusinessId_IsRejected(string businessId)
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-blank"));

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => CommandService.StartWorkflow("biz-blank", businessId: businessId));
    }

    [TestMethod]
    [DataRow(" ORDER-1")]
    [DataRow("ORDER-1 ")]
    public async Task StartWorkflow_WithPaddedBusinessId_IsRejected(string businessId)
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-padded"));

        // Act + Assert — rejected rather than trimmed, so exact-match lookups always find the stored id
        await Assert.ThrowsAsync<ArgumentException>(
            () => CommandService.StartWorkflow("biz-padded", businessId: businessId));
    }

    [TestMethod]
    [DataRow("ORDER-1\nforged log line")]
    [DataRow("ORDER-1\r\n")]
    [DataRow("ORDER\t1")]
    [DataRow("ORDER\u001b[31m1")]
    public async Task StartWorkflow_WithControlCharactersInBusinessId_IsRejected(string businessId)
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-control"));

        // Act + Assert — the id goes into every log scope; CR/LF would forge log lines
        await Assert.ThrowsAsync<ArgumentException>(
            () => CommandService.StartWorkflow("biz-control", businessId: businessId));
    }

    [TestMethod]
    public async Task StartWorkflow_WithOverlongBusinessId_IsRejected()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-long"));
        var businessId = new string('x', BusinessIdRules.MaxLength + 1);

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => CommandService.StartWorkflow("biz-long", businessId: businessId));
    }

    [TestMethod]
    public async Task StartWorkflow_WithInvalidBusinessId_LeavesTheGrainUnset()
    {
        // Arrange — call the grain directly, bypassing the command service's validation
        await DeployAsync(CreateSimpleWorkflow("biz-direct"));
        var definition = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("biz-direct").GetLatestDefinition();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() => grain.StartWorkflow(definition, businessId: "  "));
        await grain.StartWorkflow(definition, businessId: "ORDER-RETRY");

        // Assert
        Assert.AreEqual("ORDER-RETRY", await grain.GetBusinessId());
    }

    [TestMethod]
    public async Task StartWorkflow_WithUnknownStartActivity_LeavesTheGrainRetryable()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-bad-start"));
        var definition = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("biz-bad-start").GetLatestDefinition();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act — the first start fails after validation passed; nothing may be left behind
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.StartWorkflow(definition, startActivityId: "no-such-activity", businessId: "ORDER-1"));
        await grain.StartWorkflow(definition, businessId: "ORDER-2");

        // Assert
        Assert.AreEqual("ORDER-2", await grain.GetBusinessId());
        var snapshot = await QueryService.GetStateSnapshot(await grain.GetWorkflowInstanceId());
        Assert.IsNotNull(snapshot);
        Assert.AreEqual("ORDER-2", snapshot.BusinessId);
    }

    [TestMethod]
    public async Task MessageStartListener_WithInvalidBusinessId_ThrowsInsteadOfReportingNotDelivered()
    {
        // Arrange
        await DeployMessageStartWorkflow("biz-message-invalid", "bizInvalidPlaced");

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await StartEvents.StartByMessage("bizInvalidPlaced", new ExpandoObject(), ""));
    }

    [TestMethod]
    public async Task StartWorkflowByProcessDefinitionId_WithBusinessId_SetsIt()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-by-id"));
        var definition = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("biz-by-id").GetLatestDefinition();

        // Act
        var instanceId = await CommandService.StartWorkflowByProcessDefinitionId(definition.ProcessDefinitionId!, "ORDER-UI");

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual("ORDER-UI", snapshot.BusinessId);
    }

    [TestMethod]
    public async Task InstanceListing_BusinessIdParameter_MatchesSieveMetacharactersExactly()
    {
        // Arrange — `,` and `|` would split a Sieve `filters` term
        await DeployAsync(CreateSimpleWorkflow("biz-meta"));
        var match = await CommandService.StartWorkflow("biz-meta", businessId: "ACME, Inc|42");
        var other = await CommandService.StartWorkflow("biz-meta", businessId: "ACME");
        await WaitForProjection(match, other);

        // Act
        var byKey = await QueryService.GetInstancesByKey("biz-meta", new PageRequest(), "ACME, Inc|42");
        var byVersion = await QueryService.GetInstancesByKeyAndVersion("biz-meta", 1, new PageRequest(), "ACME, Inc|42");

        // Assert
        Assert.AreEqual(match, byKey.Items.Single().InstanceId);
        Assert.AreEqual(match, byVersion.Items.Single().InstanceId);
    }

    [TestMethod]
    [DataRow("  ")]
    [DataRow(" ORDER-1")]
    public async Task InstanceListing_InvalidBusinessIdParameter_IsRejected(string businessId)
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-filter-invalid"));

        // Act + Assert — a 400 at the API instead of an empty page
        await Assert.ThrowsAsync<ArgumentException>(
            () => QueryService.GetInstancesByKey("biz-filter-invalid", new PageRequest(), businessId));
    }

    [TestMethod]
    public async Task SameBusinessIdTwice_StartsBoth()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-dup"));

        // Act
        var first = await CommandService.StartWorkflow("biz-dup", businessId: "ORDER-DUP");
        var second = await CommandService.StartWorkflow("biz-dup", businessId: "ORDER-DUP");

        // Assert
        Assert.AreNotEqual(first, second);
        await WaitForProjection(first, second);
        var page = await QueryService.GetInstancesByKey("biz-dup", new PageRequest(Filters: "BusinessId==ORDER-DUP"));
        Assert.AreEqual(2, page.TotalCount);
    }

    [TestMethod]
    public async Task MessageStart_WithBusinessId_SetsItOnTheStartedInstance()
    {
        // Arrange
        await DeployMessageStartWorkflow("biz-message", "bizOrderPlaced");

        // Act
        var result = await CommandService.SendMessage("bizOrderPlaced", null, new ExpandoObject(), businessId: "ORDER-MSG");

        // Assert
        Assert.IsTrue(result.Delivered);
        Assert.IsNotNull(result.WorkflowInstanceIds);
        var snapshot = await QueryService.GetStateSnapshot(result.WorkflowInstanceIds.Single());
        Assert.IsNotNull(snapshot);
        Assert.AreEqual("ORDER-MSG", snapshot.BusinessId);
    }

    [TestMethod]
    public async Task CallActivity_ChildDoesNotInheritParentBusinessId()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-child"));
        var start = new StartEvent("start");
        var call = new CallActivity("call1", "biz-child", [], []);
        var end = new EndEvent("end");
        await DeployAsync(new WorkflowDefinition
        {
            WorkflowId = "biz-parent",
            Activities = [start, call, end],
            SequenceFlows = [new SequenceFlow("p1", start, call), new SequenceFlow("p2", call, end)]
        });

        // Act
        var parentId = await CommandService.StartWorkflow("biz-parent", businessId: "ORDER-PARENT");

        // Assert
        var parent = await WaitForCondition(parentId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "call1" && a.ChildWorkflowInstanceId is not null));
        var childId = parent.ActiveActivities.Single(a => a.ActivityId == "call1").ChildWorkflowInstanceId!.Value;
        var child = await QueryService.GetStateSnapshot(childId);
        Assert.IsNotNull(child);
        Assert.AreEqual("ORDER-PARENT", parent.BusinessId);
        Assert.IsNull(child.BusinessId);
    }

    // WorkflowInstance "Workflow started" (3007), the worker's "Handling custom-task event" (4030)
    // and the script executor's "executing" (9020) — CapturedLogEvents keeps their message and scope fields.
    private const int WorkflowStartedEventId = 3007;
    private const int CustomTaskHandlingEventId = 4030;
    private const int ScriptHandlingEventId = 9020;

    [TestMethod]
    [DoNotParallelize]
    public async Task WorkflowStartedLog_CarriesInstanceIdAndBusinessId_InMessageAndScope()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-log"));
        using var capture = CapturedLogEvents.Capture(WorkflowStartedEventId);

        // Act
        var withId = await CommandService.StartWorkflow("biz-log", businessId: "ORDER-LOG");
        var withoutId = await CommandService.StartWorkflow("biz-log");

        // Assert — the message names the instance id the API returned, not an internal one
        var entries = CapturedLogEvents.Entries(WorkflowStartedEventId);
        var started = entries.Single(e => e.Message.Contains(withId.ToString()));
        StringAssert.Contains(started.Message, "BusinessId=ORDER-LOG");
        Assert.AreEqual("ORDER-LOG", started.ScopeFields["BusinessId"]);
        Assert.AreEqual(withId.ToString(), started.ScopeFields["WorkflowInstanceId"]);

        var startedWithout = entries.Single(e => e.Message.Contains(withoutId.ToString()));
        StringAssert.Contains(startedWithout.Message, "BusinessId=-");
        Assert.AreEqual("-", startedWithout.ScopeFields["BusinessId"]);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task CustomTaskWorkerLog_CarriesBusinessIdInScope()
    {
        // Arrange
        var start = new StartEvent("start");
        var ct1 = new CustomTaskActivity("ct1", "test-no-op-a", null, null);
        var end = new EndEvent("end");
        await DeployAsync(new WorkflowDefinition
        {
            WorkflowId = "biz-log-worker",
            Activities = [start, ct1, end],
            SequenceFlows = [new SequenceFlow("f1", start, ct1), new SequenceFlow("f2", ct1, end)]
        });
        using var capture = CapturedLogEvents.Capture(CustomTaskHandlingEventId);

        // Act
        var instanceId = await CommandService.StartWorkflow("biz-log-worker", businessId: "ORDER-WORKER");
        await WaitForCondition(instanceId, s => s.IsCompleted);

        // Assert — the stream is at-least-once, so the handler may log more than once
        var handling = CapturedLogEvents.Entries(CustomTaskHandlingEventId)
            .Where(e => Equals(e.ScopeFields["WorkflowInstanceId"], instanceId.ToString()))
            .ToList();
        Assert.IsNotEmpty(handling);
        Assert.IsTrue(handling.All(e => Equals(e.ScopeFields["BusinessId"], "ORDER-WORKER")));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ScriptExecutorLog_KeepsBusinessIdArrivingWithTheDispatch()
    {
        // Arrange — the script dispatch does not carry the business id; it arrives in RequestContext
        var start = new StartEvent("start");
        var script = new ScriptTask("script1", "noop", "csharp");
        var end = new EndEvent("end");
        await DeployAsync(new WorkflowDefinition
        {
            WorkflowId = "biz-log-script",
            Activities = [start, script, end],
            SequenceFlows = [new SequenceFlow("f1", start, script), new SequenceFlow("f2", script, end)]
        });
        using var capture = CapturedLogEvents.Capture(ScriptHandlingEventId);

        // Act
        var instanceId = await CommandService.StartWorkflow("biz-log-script", businessId: "ORDER-SCRIPT");
        await WaitForCondition(instanceId, s => s.IsCompleted);

        // Assert
        var handling = CapturedLogEvents.Entries(ScriptHandlingEventId)
            .Where(e => Equals(e.ScopeFields["WorkflowInstanceId"], instanceId.ToString()))
            .ToList();
        Assert.IsNotEmpty(handling);
        Assert.IsTrue(handling.All(e => Equals(e.ScopeFields["BusinessId"], "ORDER-SCRIPT")));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task CallActivityChild_WorkflowStartedLog_DoesNotInheritParentBusinessId()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-log-child"));
        var start = new StartEvent("start");
        var call = new CallActivity("call1", "biz-log-child", [], []);
        var end = new EndEvent("end");
        await DeployAsync(new WorkflowDefinition
        {
            WorkflowId = "biz-log-parent",
            Activities = [start, call, end],
            SequenceFlows = [new SequenceFlow("p1", start, call), new SequenceFlow("p2", call, end)]
        });
        using var capture = CapturedLogEvents.Capture(WorkflowStartedEventId);

        // Act
        var parentId = await CommandService.StartWorkflow("biz-log-parent", businessId: "ORDER-PARENT-LOG");

        // Assert
        var parent = await WaitForCondition(parentId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "call1" && a.ChildWorkflowInstanceId is not null)
                 || s.IsCompleted);
        var childId = (parent.ActiveActivities.SingleOrDefault(a => a.ActivityId == "call1")
            ?? parent.CompletedActivities.Single(a => a.ActivityId == "call1")).ChildWorkflowInstanceId!.Value;
        var childStarted = CapturedLogEvents.Entries(WorkflowStartedEventId)
            .Single(e => e.Message.Contains(childId.ToString()));
        StringAssert.Contains(childStarted.Message, "BusinessId=-");
        Assert.AreEqual("-", childStarted.ScopeFields["BusinessId"]);
    }

    private Task DeployAsync(IWorkflowDefinition workflow) =>
        Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .DeployVersion((WorkflowDefinition)workflow, "<placeholder/>");

    private Task DeployMessageStartWorkflow(string workflowId, string messageName)
    {
        var messageStart = new MessageStartEvent("msgStart", "msg1");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        return DeployAsync(new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [messageStart, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", messageStart, task),
                new SequenceFlow("f2", task, end)
            ],
            Messages = [new MessageDefinition("msg1", messageName, null)]
        });
    }
}
