using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// #824 — business id on process instances, with uniqueness enforcement off (the default).
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
    public async Task UniquenessOff_SameBusinessIdTwice_StartsBoth()
    {
        // Arrange
        await DeployAsync(CreateSimpleWorkflow("biz-dup"));

        // Act
        var first = await CommandService.StartWorkflow("biz-dup", businessId: "ORDER-DUP");
        var second = await CommandService.StartWorkflow("biz-dup", businessId: "ORDER-DUP");

        // Assert
        Assert.AreNotEqual(first, second);
        var page = await QueryService.GetInstancesByKey("biz-dup", new PageRequest(Filters: "BusinessId==ORDER-DUP"));
        Assert.AreEqual(2, page.TotalCount);
    }

    [TestMethod]
    public async Task MessageStart_WithBusinessId_SetsItOnTheStartedInstance()
    {
        // Arrange
        var messageStart = new MessageStartEvent("msgStart", "msg1");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        await DeployAsync(new WorkflowDefinition
        {
            WorkflowId = "biz-message",
            Activities = [messageStart, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", messageStart, task),
                new SequenceFlow("f2", task, end)
            ],
            Messages = [new MessageDefinition("msg1", "bizOrderPlaced", null)]
        });

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

    private Task DeployAsync(IWorkflowDefinition workflow) =>
        Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .DeployVersion((WorkflowDefinition)workflow, "<placeholder/>");
}

/// <summary>
/// #824 — business id uniqueness, enabled through <c>Fleans:BusinessId:EnforceUniqueness</c>.
/// </summary>
[TestClass]
public class BusinessIdUniquenessTests : WorkflowTestBase
{
    private IWorkflowCommandService CommandService => GetSiloService<IWorkflowCommandService>();

    protected override void ConfigureCluster(TestClusterBuilder builder) =>
        builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{BusinessIdOptions.SectionName}:{nameof(BusinessIdOptions.EnforceUniqueness)}"] = "true"
        }));

    [TestMethod]
    public async Task SecondStart_WhileFirstIsActive_IsRejectedWithConflict()
    {
        // Arrange
        await DeployAsync("uniq-active");
        var first = await CommandService.StartWorkflow("uniq-active", businessId: "ORDER-1");

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CommandService.StartWorkflow("uniq-active", businessId: "ORDER-1"));

        // Assert
        StringAssert.Contains(ex.Message, first.ToString());
        var page = await QueryService.GetInstancesByKey("uniq-active", new PageRequest(Filters: "BusinessId==ORDER-1"));
        Assert.AreEqual(1, page.TotalCount, "The rejected start must not leave an instance behind");
    }

    [TestMethod]
    public async Task Start_AfterPreviousInstanceCompleted_IsAllowed()
    {
        // Arrange
        await DeployAsync("uniq-done");
        var first = await CommandService.StartWorkflow("uniq-done", businessId: "ORDER-1");
        await CommandService.CompleteActivity(first, "task", new ExpandoObject());
        await WaitForCondition(first, s => s.IsCompleted);

        // Act
        var second = await CommandService.StartWorkflow("uniq-done", businessId: "ORDER-1");

        // Assert
        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public async Task SameBusinessId_OnDifferentProcesses_IsAllowed()
    {
        // Arrange
        await DeployAsync("uniq-a");
        await DeployAsync("uniq-b");

        // Act
        var a = await CommandService.StartWorkflow("uniq-a", businessId: "ORDER-1");
        var b = await CommandService.StartWorkflow("uniq-b", businessId: "ORDER-1");

        // Assert
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public async Task StartsWithoutBusinessId_AreNotConstrained()
    {
        // Arrange
        await DeployAsync("uniq-none");

        // Act
        var first = await CommandService.StartWorkflow("uniq-none");
        var second = await CommandService.StartWorkflow("uniq-none");

        // Assert
        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public async Task ConcurrentStarts_WithSameBusinessId_StartExactlyOne()
    {
        // Arrange
        await DeployAsync("uniq-race");

        // Act
        var attempts = Enumerable.Range(0, 5)
            .Select(async _ =>
            {
                try { return (Guid?)await CommandService.StartWorkflow("uniq-race", businessId: "ORDER-RACE"); }
                catch (InvalidOperationException) { return null; }
            })
            .ToList();
        var results = await Task.WhenAll(attempts);

        // Assert
        Assert.AreEqual(1, results.Count(r => r.HasValue));
        var page = await QueryService.GetInstancesByKey("uniq-race", new PageRequest(Filters: "BusinessId==ORDER-RACE"));
        Assert.AreEqual(1, page.TotalCount);
    }

    [TestMethod]
    public async Task MessageStart_WithBusinessIdAlreadyActive_CreatesNoSecondInstance()
    {
        // Arrange
        var messageStart = new MessageStartEvent("msgStart", "msg1");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("uniq-message").DeployVersion(new WorkflowDefinition
        {
            WorkflowId = "uniq-message",
            Activities = [messageStart, task, end],
            SequenceFlows = [new SequenceFlow("f1", messageStart, task), new SequenceFlow("f2", task, end)],
            Messages = [new MessageDefinition("msg1", "uniqOrderPlaced", null)]
        }, "<placeholder/>");
        var first = await CommandService.SendMessage("uniqOrderPlaced", null, new ExpandoObject(), businessId: "ORDER-1");

        // Act
        var second = await CommandService.SendMessage("uniqOrderPlaced", null, new ExpandoObject(), businessId: "ORDER-1");

        // Assert
        Assert.IsTrue(first.Delivered);
        Assert.IsFalse(second.Delivered);
        var page = await QueryService.GetInstancesByKey("uniq-message", new PageRequest(Filters: "BusinessId==ORDER-1"));
        Assert.AreEqual(1, page.TotalCount);
    }

    private Task DeployAsync(string workflowId) =>
        Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflowId)
            .DeployVersion((WorkflowDefinition)CreateSimpleWorkflow(workflowId), "<placeholder/>");
}
