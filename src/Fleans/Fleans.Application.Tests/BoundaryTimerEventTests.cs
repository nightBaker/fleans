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
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "task1"),
            "Original task should be completed (interrupted)");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "recovery"),
            "Recovery task should be active");

        // Complete recovery
        await workflowInstance.CompleteActivity("recovery", new ExpandoObject());

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "timeoutEnd"),
            "Should complete via timeout end");
    }
}
