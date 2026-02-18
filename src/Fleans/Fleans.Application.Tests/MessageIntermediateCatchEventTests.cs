using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageIntermediateCatchEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task MessageCatch_ShouldSuspendWorkflow_UntilMessageDelivered()
    {
        // Arrange — Start → Task → MessageCatch → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "paymentReceived", "orderId");
        var msgCatch = new MessageIntermediateCatchEvent("waitPayment", "msg1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "msg-catch-test",
            Activities = [start, task, msgCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, msgCatch),
                new SequenceFlow("f3", msgCatch, end)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task with orderId variable
        dynamic vars = new ExpandoObject();
        vars.orderId = "order-123";
        await workflowInstance.CompleteActivity("task1", vars);

        // Assert — workflow suspended at message catch
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should NOT be completed — waiting for message");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "waitPayment"),
            "Message catch activity should be active");

        // Act — deliver message via correlation grain
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("paymentReceived");
        dynamic msgVars = new ExpandoObject();
        msgVars.paymentStatus = "confirmed";
        var delivered = await correlationGrain.DeliverMessage("order-123", (ExpandoObject)msgVars);

        // Assert — workflow completed, variables merged
        Assert.IsTrue(delivered, "Message should be delivered successfully");
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after message delivery");
    }

    [TestMethod]
    public async Task MessageCatch_WrongCorrelationKey_ShouldNotDeliver()
    {
        // Arrange
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "paymentReceived", "orderId");
        var msgCatch = new MessageIntermediateCatchEvent("waitPayment", "msg1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "msg-catch-wrong-key",
            Activities = [start, task, msgCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, msgCatch),
                new SequenceFlow("f3", msgCatch, end)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic vars = new ExpandoObject();
        vars.orderId = "order-123";
        await workflowInstance.CompleteActivity("task1", vars);

        // Act — deliver with wrong key
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("paymentReceived");
        var delivered = await correlationGrain.DeliverMessage("order-999", new ExpandoObject());

        // Assert — not delivered, workflow still waiting
        Assert.IsFalse(delivered, "Should not find a matching subscription");
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should still be waiting");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "waitPayment"),
            "Message catch should still be active");
    }
}
