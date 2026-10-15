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
    public async Task SetWorkflow_WithInvalidBusinessId_LeavesTheGrainUnset()
    {
        // Arrange — call the grain directly, bypassing the command service's validation
        await DeployAsync(CreateSimpleWorkflow("biz-direct"));
        var definition = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("biz-direct").GetLatestDefinition();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() => grain.SetWorkflow(definition, businessId: "  "));
        await grain.SetWorkflow(definition, businessId: "ORDER-RETRY");

        // Assert
        Assert.AreEqual("ORDER-RETRY", await grain.GetBusinessId());
    }

    [TestMethod]
    public async Task MessageStartListener_WithInvalidBusinessId_ThrowsInsteadOfReportingNotDelivered()
    {
        // Arrange
        await DeployMessageStartWorkflow("biz-message-invalid", "bizInvalidPlaced");
        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>("bizInvalidPlaced");

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await listener.FireMessageStartEvent(new ExpandoObject(), ""));
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
        await CommandService.StartWorkflow("biz-meta", businessId: "ACME");

        // Act
        var byKey = await QueryService.GetInstancesByKey("biz-meta", new PageRequest(), "ACME, Inc|42");
        var byVersion = await QueryService.GetInstancesByKeyAndVersion("biz-meta", 1, new PageRequest(), "ACME, Inc|42");

        // Assert
        Assert.AreEqual(match, byKey.Items.Single().InstanceId);
        Assert.AreEqual(match, byVersion.Items.Single().InstanceId);
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
