using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageBoundaryEventTests : BoundaryEventTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new MessageBoundaryEvent(boundaryId, attachedToId, "msg1", IsInterrupting: isInterrupting);

    protected override List<MessageDefinition> GetMessageDefinitions()
        => [new MessageDefinition("msg1", "cancelOrder", "orderId")];

    protected override async Task SetupInitialState(IWorkflowInstanceGrain instance)
    {
        dynamic initVars = new ExpandoObject();
        initVars.orderId = "order-456";
        await instance.SetInitialVariables(initVars);
    }

    protected override async Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId)
    {
        var grainKey = MessageCorrelationKey.Build("cancelOrder", "order-456");
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
        var delivered = await correlationGrain.DeliverMessage(new ExpandoObject());
        Assert.IsTrue(delivered, "Message should be delivered");
    }

    [TestMethod]
    public async Task NonInterruptingBoundaryMessage_AttachedActivityContinues()
    {
        // Arrange — Start → Task(+NonInterruptingBoundaryMessage) → End, BoundaryMsg → afterMsg → MsgEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "cancelOrder", "orderId");
        var boundaryMsg = new MessageBoundaryEvent("bmsg1", "task1", "msg1", IsInterrupting: false);
        var end = new EndEvent("end");
        var afterMsg = new TaskActivity("afterMsg");
        var msgEnd = new EndEvent("msgEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ni-msg-boundary-test",
            Activities = [start, task, boundaryMsg, end, afterMsg, msgEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryMsg, afterMsg),
                new SequenceFlow("f4", afterMsg, msgEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.orderId = "order-ni-msg";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        // Verify task is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "Task should be active");

        // Act — deliver boundary message (non-interrupting)
        var grainKey = MessageCorrelationKey.Build("cancelOrder", "order-ni-msg");
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
        var delivered = await correlationGrain.DeliverMessage(new ExpandoObject());

        // Assert — task1 should still be active (NOT cancelled)
        Assert.IsTrue(delivered, "Message should be delivered");
        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(midSnapshot!.IsCompleted, "Workflow should not be completed yet");
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "task1 should still be active after non-interrupting message");
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "afterMsg"),
            "afterMsg should be active on boundary path");
        Assert.IsFalse(midSnapshot.CompletedActivities.Any(a => a.ActivityId == "task1" && a.IsCancelled),
            "task1 should NOT be cancelled");

        // Complete the attached activity normally
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Workflow may now be completed (task1 → end)
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");
        var task1Entry = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "task1");
        Assert.IsFalse(task1Entry.IsCancelled, "task1 should NOT be cancelled");
    }

    [TestMethod]
    public async Task BoundaryMessage_DirectCallAfterCompletion_ShouldBeSilentlyIgnored()
    {
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

        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.IsCompleted);

        await workflowInstance.HandleBoundaryMessageFired("bmsg1", hostInstanceId);

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
    }
}
