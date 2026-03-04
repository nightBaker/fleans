using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class BoundaryTimerEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task BoundaryTimer_ActivityCompletesFirst_ShouldFollowNormalFlow()
    {
        // Arrange — Start -> Task(+BoundaryTimer) -> End, BoundaryTimer -> TimeoutEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var end = new EndEvent("end");
        var timeoutEnd = new EndEvent("timeoutEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-timer-test",
            Activities = [start, task, boundaryTimer, end, timeoutEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryTimer, timeoutEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — task completes before timer fires
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — workflow completes via normal end, not timeout end
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should complete via normal end event");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "timeoutEnd"),
            "Should NOT complete via timeout end event");
    }

    [TestMethod]
    public async Task BoundaryTimer_TimerFiresFirst_ShouldFollowBoundaryFlow()
    {
        // Arrange — Start -> Task(+BoundaryTimer) -> End, BoundaryTimer -> Recovery -> TimeoutEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var end = new EndEvent("end");
        var recovery = new TaskActivity("recovery");
        var timeoutEnd = new EndEvent("timeoutEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-timer-fire-test",
            Activities = [start, task, boundaryTimer, end, recovery, timeoutEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryTimer, recovery),
                new SequenceFlow("f4", recovery, timeoutEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Verify task is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"));

        // Act — simulate boundary timer firing via HandleTimerFired
        var hostInstanceId = preSnapshot.ActiveActivities.First(a => a.ActivityId == "task1").ActivityInstanceId;
        await workflowInstance.HandleTimerFired("bt1", hostInstanceId);

        // Assert — should follow boundary path, task1 should be interrupted
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed yet — recovery task is pending");
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
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "timeoutEnd"),
            "Should complete via timeout end");
    }

    [TestMethod]
    public async Task NonInterruptingBoundaryTimer_AttachedActivityContinues()
    {
        // Arrange — workflow with task1 + non-interrupting timer boundary bt1
        // start -> task1 -> end1
        //          bt1 -> afterTimer -> end2
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef, IsInterrupting: false);
        var afterTimer = new TaskActivity("afterTimer");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ni-timer-test",
            Activities = [start, task, boundaryTimer, afterTimer, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end1),
                new SequenceFlow("f3", boundaryTimer, afterTimer),
                new SequenceFlow("f4", afterTimer, end2)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Get host activity instance ID for timer callback
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        // Act — simulate timer firing (non-interrupting)
        await workflowInstance.HandleTimerFired("bt1", hostInstanceId);

        // Assert: task1 should still be active (not cancelled), afterTimer should be active too
        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(midSnapshot!.IsCompleted, "Workflow should not be completed yet");
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "task1 should still be active after non-interrupting timer");
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "afterTimer"),
            "afterTimer should be active on the boundary path");
        // task1 must NOT appear in completed activities as cancelled
        Assert.IsFalse(midSnapshot.CompletedActivities.Any(a => a.ActivityId == "task1" && a.IsCancelled),
            "task1 should NOT be cancelled by non-interrupting timer");

        // Complete the attached activity normally — it should complete without error
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert: task1 should be completed normally (NOT cancelled)
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after task1 reaches end1");
        var task1Entry = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "task1");
        Assert.IsFalse(task1Entry.IsCancelled, "task1 should NOT be cancelled");
    }

    [TestMethod]
    public async Task InterruptingBoundaryTimer_StillCancelsAttachedActivity()
    {
        // Regression test: same structure as non-interrupting test but with IsInterrupting: true (default)
        // Verify that interrupting behavior still works — task1 gets cancelled when timer fires
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef); // default IsInterrupting: true
        var afterTimer = new TaskActivity("afterTimer");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "i-timer-regression",
            Activities = [start, task, boundaryTimer, afterTimer, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end1),
                new SequenceFlow("f3", boundaryTimer, afterTimer),
                new SequenceFlow("f4", afterTimer, end2)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        // Act — simulate timer firing (interrupting)
        await workflowInstance.HandleTimerFired("bt1", hostInstanceId);

        // Assert: task1 should be cancelled
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        var task1Entry = snapshot!.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(task1Entry, "task1 should be completed (interrupted)");
        Assert.IsTrue(task1Entry.IsCancelled, "task1 should be cancelled by interrupting timer");
    }
}
