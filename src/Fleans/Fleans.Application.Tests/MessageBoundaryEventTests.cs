using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageBoundaryEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task BoundaryMessage_MessageArrivesFirst_ShouldFollowBoundaryFlow()
    {
        // Arrange — Start → Task(+BoundaryMessage) → End, BoundaryMessage → Recovery → MsgEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "cancelOrder", "orderId");
        var boundaryMsg = new MessageBoundaryEvent("bmsg1", "task1", "msg1");
        var end = new EndEvent("end");
        var recovery = new TaskActivity("recovery");
        var msgEnd = new EndEvent("msgEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-msg-test",
            Activities = [start, task, boundaryMsg, end, recovery, msgEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryMsg, recovery),
                new SequenceFlow("f4", recovery, msgEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.orderId = "order-456";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        // Verify task is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "Task should be active");

        // Act — deliver boundary message
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("cancelOrder");
        var delivered = await correlationGrain.DeliverMessage("order-456", new ExpandoObject());

        // Assert — boundary path taken, task interrupted
        Assert.IsTrue(delivered, "Message should be delivered");
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should NOT be completed yet — recovery pending");
        var interruptedTask = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(interruptedTask, "Original task should be completed (interrupted)");
        Assert.IsTrue(interruptedTask.IsCancelled, "Interrupted task should be cancelled");
        Assert.IsNotNull(interruptedTask.CancellationReason, "Cancelled task should have a reason");
        Assert.IsNull(interruptedTask.ErrorState, "Cancelled task should not have error state");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "recovery"),
            "Recovery task should be active");

        // Complete recovery
        await workflowInstance.CompleteActivity("recovery", new ExpandoObject());
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after recovery");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "msgEnd"),
            "Should complete via message end");
    }

    [TestMethod]
    public async Task BoundaryMessage_TaskCompletesFirst_ShouldFollowNormalFlow()
    {
        // Arrange
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "cancelOrder", "orderId");
        var boundaryMsg = new MessageBoundaryEvent("bmsg1", "task1", "msg1");
        var end = new EndEvent("end");
        var msgEnd = new EndEvent("msgEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-msg-normal",
            Activities = [start, task, boundaryMsg, end, msgEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryMsg, msgEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.orderId = "order-789";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        // Act — complete task normally
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — normal flow, subscription cleaned up
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed via normal flow");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should complete via normal end event");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "msgEnd"),
            "Should NOT complete via message end event");

        // Verify subscription is gone
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("cancelOrder");
        var delivered = await correlationGrain.DeliverMessage("order-789", new ExpandoObject());
        Assert.IsFalse(delivered, "Subscription should have been cleaned up");
    }

    [TestMethod]
    public async Task BoundaryMessage_DirectCallAfterCompletion_ShouldBeSilentlyIgnored()
    {
        // Arrange — Start → Task(+BoundaryMessage) → End, BoundaryMessage → MsgEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "cancelOrder", "orderId");
        var boundaryMsg = new MessageBoundaryEvent("bmsg1", "task1", "msg1");
        var end = new EndEvent("end");
        var msgEnd = new EndEvent("msgEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-msg-stale",
            Activities = [start, task, boundaryMsg, end, msgEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryMsg, msgEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.orderId = "order-stale";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        // Get the host activity instance ID before completing
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities.First(a => a.ActivityId == "task1").ActivityInstanceId;

        // Complete task normally — host activity is now done
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.IsCompleted, "Workflow should be completed via normal flow");

        // Act — simulate stale boundary message with the old hostActivityInstanceId
        // This should be silently ignored (no error, no state change)
        await workflowInstance.HandleBoundaryMessageFired("bmsg1", hostInstanceId);

        // Assert — workflow is still completed, no crash
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should still be completed after stale message");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should still be completed via normal end event");
    }
}
