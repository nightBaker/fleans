using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalBoundaryEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task BoundarySignal_SignalArrivesFirst_ShouldFollowBoundaryFlow()
    {
        // Arrange — Start → Task(+BoundarySignal) → End, BoundarySignal → Recovery → SigEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "cancelOrder");
        var boundarySignal = new SignalBoundaryEvent("bsig1", "task1", "sig1");
        var end = new EndEvent("end");
        var recovery = new TaskActivity("recovery");
        var sigEnd = new EndEvent("sigEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-signal-test",
            Activities = [start, task, boundarySignal, end, recovery, sigEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundarySignal, recovery),
                new SequenceFlow("f4", recovery, sigEnd)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Verify task is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "Task should be active");

        // Act — broadcast boundary signal
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("cancelOrder");
        var deliveredCount = await signalGrain.BroadcastSignal();

        // Assert — boundary path taken, task interrupted
        Assert.AreEqual(1, deliveredCount, "Signal should be delivered");
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
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sigEnd"),
            "Should complete via signal end");
    }

    [TestMethod]
    public async Task BoundarySignal_TaskCompletesFirst_ShouldFollowNormalFlow()
    {
        // Arrange
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "cancelOrder");
        var boundarySignal = new SignalBoundaryEvent("bsig1", "task1", "sig1");
        var end = new EndEvent("end");
        var sigEnd = new EndEvent("sigEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-signal-normal",
            Activities = [start, task, boundarySignal, end, sigEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundarySignal, sigEnd)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — complete task normally
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — normal flow, subscription cleaned up
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed via normal flow");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should complete via normal end event");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "sigEnd"),
            "Should NOT complete via signal end event");

        // Verify subscription is gone — broadcast should deliver 0
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("cancelOrder");
        var deliveredCount = await signalGrain.BroadcastSignal();
        Assert.AreEqual(0, deliveredCount, "Subscription should have been cleaned up");
    }

    [TestMethod]
    public async Task BoundarySignal_StaleSignal_ShouldBeSilentlyIgnored()
    {
        // Arrange — Start → Task(+BoundarySignal) → End, BoundarySignal → SigEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "cancelOrder");
        var boundarySignal = new SignalBoundaryEvent("bsig1", "task1", "sig1");
        var end = new EndEvent("end");
        var sigEnd = new EndEvent("sigEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-signal-stale",
            Activities = [start, task, boundarySignal, end, sigEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundarySignal, sigEnd)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Get the host activity instance ID before completing
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities.First(a => a.ActivityId == "task1").ActivityInstanceId;

        // Complete task normally — host activity is now done
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.IsCompleted, "Workflow should be completed via normal flow");

        // Act — simulate stale boundary signal with the old hostActivityInstanceId
        // This should be silently ignored (no error, no state change)
        await workflowInstance.HandleBoundarySignalFired("bsig1", hostInstanceId);

        // Assert — workflow is still completed, no crash
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should still be completed after stale signal");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should still be completed via normal end event");
    }
}
