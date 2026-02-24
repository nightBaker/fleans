using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SubProcessTests : WorkflowTestBase
{
    [TestMethod]
    public async Task SubProcess_SimpleFlow_ShouldCompleteThrough()
    {
        var start = new StartEvent("start");
        var innerStart = new StartEvent("sub_start");
        var innerTask = new TaskActivity("sub_task");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("inner_f1", innerStart, innerTask),
                new SequenceFlow("inner_f2", innerTask, innerEnd)
            ]
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-process-simple",
            Activities = [start, subProcess, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, subProcess),
                new SequenceFlow("f2", subProcess, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Should be suspended at sub_task");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "sub_task"));

        await workflowInstance.CompleteActivity("sub_task", new ExpandoObject());

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Should be completed");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sub_start"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sub_task"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sub_end"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
    }

    [TestMethod]
    public async Task SubProcess_VariableInheritance_ShouldReadParentVariable()
    {
        var start = new StartEvent("start");
        var parentTask = new TaskActivity("parentTask");
        var innerStart = new StartEvent("sub_start");
        var innerTask = new TaskActivity("sub_task");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("inner_f1", innerStart, innerTask),
                new SequenceFlow("inner_f2", innerTask, innerEnd)
            ]
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-process-var-inherit",
            Activities = [start, parentTask, subProcess, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, parentTask),
                new SequenceFlow("f2", parentTask, subProcess),
                new SequenceFlow("f3", subProcess, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic vars = new ExpandoObject();
        vars.color = "red";
        await workflowInstance.CompleteActivity("parentTask", vars);

        // Workflow should now be paused at sub_task (inside sub-process child scope)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        var subTaskSnapshot = snapshot!.ActiveActivities.First(a => a.ActivityId == "sub_task");

        // sub_task is in a child scope — GetVariable should walk up to parent and find "color"
        var color = await workflowInstance.GetVariable(subTaskSnapshot.VariablesStateId, "color");
        Assert.AreEqual("red", color, "Child scope should read parent variable via walk-up");

        await workflowInstance.CompleteActivity("sub_task", new ExpandoObject());

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should complete");
    }

    [TestMethod]
    public async Task SubProcess_VariableShadowing_ShouldNotAffectParent()
    {
        var start = new StartEvent("start");
        var parentTask = new TaskActivity("parentTask");
        var innerStart = new StartEvent("sub_start");
        var innerTask = new TaskActivity("sub_task");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("inner_f1", innerStart, innerTask),
                new SequenceFlow("inner_f2", innerTask, innerEnd)
            ]
        };
        var checkTask = new TaskActivity("checkTask");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-process-var-shadow",
            Activities = [start, parentTask, subProcess, checkTask, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, parentTask),
                new SequenceFlow("f2", parentTask, subProcess),
                new SequenceFlow("f3", subProcess, checkTask),
                new SequenceFlow("f4", checkTask, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic parentVars = new ExpandoObject();
        parentVars.color = "red";
        await workflowInstance.CompleteActivity("parentTask", parentVars);

        // Complete sub_task with shadowed variable — writes "blue" to child scope
        dynamic childVars = new ExpandoObject();
        childVars.color = "blue";
        await workflowInstance.CompleteActivity("sub_task", childVars);

        // Workflow should now be paused at checkTask (back in parent scope)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Should be paused at checkTask");
        var checkTaskSnapshot = snapshot.ActiveActivities.First(a => a.ActivityId == "checkTask");

        // checkTask is at parent scope — "color" should still be "red" (child wrote to its own scope)
        var color = await workflowInstance.GetVariable(checkTaskSnapshot.VariablesStateId, "color");
        Assert.AreEqual("red", color, "Parent scope should not be affected by child scope writes");

        await workflowInstance.CompleteActivity("checkTask", new ExpandoObject());

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should complete");
    }

    [TestMethod]
    public async Task SubProcess_NestedSubProcess_ShouldCompleteThrough()
    {
        var start = new StartEvent("start");

        var innerStart = new StartEvent("inner_start");
        var innerTask = new TaskActivity("inner_task");
        var innerEnd = new EndEvent("inner_end");
        var innerSub = new SubProcess("inner")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("inner_f1", innerStart, innerTask),
                new SequenceFlow("inner_f2", innerTask, innerEnd)
            ]
        };

        var outerStart = new StartEvent("outer_start");
        var outerEnd = new EndEvent("outer_end");
        var outerSub = new SubProcess("outer")
        {
            Activities = [outerStart, innerSub, outerEnd],
            SequenceFlows =
            [
                new SequenceFlow("outer_f1", outerStart, innerSub),
                new SequenceFlow("outer_f2", innerSub, outerEnd)
            ]
        };

        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "nested-sub-process",
            Activities = [start, outerSub, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, outerSub),
                new SequenceFlow("f2", outerSub, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        await workflowInstance.CompleteActivity("inner_task", new ExpandoObject());

        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Nested sub-process workflow should complete");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
    }

    [TestMethod]
    public async Task SubProcess_BoundaryTimer_ShouldCancelChildActivities()
    {
        var start = new StartEvent("start");
        var innerStart = new StartEvent("sub_start");
        var innerTask = new TaskActivity("sub_task");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("inner_f1", innerStart, innerTask),
                new SequenceFlow("inner_f2", innerTask, innerEnd)
            ]
        };
        var boundaryTimer = new BoundaryTimerEvent("boundary_timer", "sub1",
            new TimerDefinition(TimerType.Duration, "PT30M"));
        var endBoundary = new EndEvent("endBoundary");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-process-boundary-timer",
            Activities = [start, subProcess, boundaryTimer, endBoundary, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, subProcess),
                new SequenceFlow("f2", subProcess, end),
                new SequenceFlow("f3", boundaryTimer, endBoundary)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.ActiveActivities.Any(a => a.ActivityId == "sub_task"));

        // Simulate boundary timer firing on the sub-process
        var hostInstanceId = snapshot.ActiveActivities.First(a => a.ActivityId == "sub1").ActivityInstanceId;
        await workflowInstance.HandleTimerFired("boundary_timer", hostInstanceId);

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Should complete via boundary path");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endBoundary"));
        Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should NOT follow normal path");

        var subTask = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "sub_task");
        Assert.IsNotNull(subTask, "sub_task should be in completed list");
        Assert.IsTrue(subTask.IsCancelled, "sub_task should be cancelled by boundary event");
    }

    [TestMethod]
    public async Task SubProcess_BoundaryError_ShouldCancelChildActivities()
    {
        var start = new StartEvent("start");
        var innerStart = new StartEvent("sub_start");
        var innerTask = new TaskActivity("sub_task");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("inner_f1", innerStart, innerTask),
                new SequenceFlow("inner_f2", innerTask, innerEnd)
            ]
        };
        var boundaryError = new BoundaryErrorEvent("boundary_error", "sub1", null);
        var endError = new EndEvent("endError");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-process-boundary-error",
            Activities = [start, subProcess, boundaryError, endError, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, subProcess),
                new SequenceFlow("f2", subProcess, end),
                new SequenceFlow("f3", boundaryError, endError)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        await workflowInstance.FailActivity("sub_task", new Exception("Something went wrong"));

        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Should complete via error boundary path");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endError"));
        Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should NOT follow normal path");
    }

    [TestMethod]
    public async Task SubProcess_NestedSubProcess_ShouldInheritVariablesAcrossThreeLevels()
    {
        // Arrange: start -> task1 -> outer_sub(outer_start -> inner_sub(inner_start -> inner_task -> inner_end) -> outer_end) -> end
        var innerStart = new StartEvent("inner_start");
        var innerTask = new TaskActivity("inner_task");
        var innerEnd = new EndEvent("inner_end");
        var innerSub = new SubProcess("inner_sub")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("isf1", innerStart, innerTask),
                new SequenceFlow("isf2", innerTask, innerEnd)
            ]
        };

        var outerStart = new StartEvent("outer_start");
        var outerEnd = new EndEvent("outer_end");
        var outerSub = new SubProcess("outer_sub")
        {
            Activities = [outerStart, innerSub, outerEnd],
            SequenceFlows =
            [
                new SequenceFlow("osf1", outerStart, innerSub),
                new SequenceFlow("osf2", innerSub, outerEnd)
            ]
        };

        var start = new StartEvent("start");
        var task1 = new TaskActivity("task1");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "nested-var-test",
            Activities = [start, task1, outerSub, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task1),
                new SequenceFlow("f2", task1, outerSub),
                new SequenceFlow("f3", outerSub, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act: complete task1 with a variable
        dynamic vars = new ExpandoObject();
        vars.rootColor = "red";
        await workflowInstance.CompleteActivity("task1", vars);

        // Assert: inner_task should be active, and rootColor should be accessible
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        var innerTaskSnapshot = snapshot!.ActiveActivities.First(a => a.ActivityId == "inner_task");

        // Walk up from innermost scope should find root variable
        var retrievedValue = await workflowInstance.GetVariable(innerTaskSnapshot.VariablesStateId, "rootColor");
        Assert.AreEqual("red", retrievedValue);
    }

    [TestMethod]
    public async Task SubProcess_EventBasedGateway_TimerWins_ShouldCancelSiblingTimer()
    {
        // Arrange: start -> subProcess(sub_start -> EBG -> [timerA(1h), timerB(2h)] -> sub_end) -> end
        // When timerA fires, timerB (the sibling inside the sub-process scope) should be cancelled.
        var sub_start = new StartEvent("sub_start");
        var ebg = new EventBasedGateway("sub_ebg");
        var timerA = new TimerIntermediateCatchEvent("sub_timerA",
            new TimerDefinition(TimerType.Duration, "PT1H"));
        var timerB = new TimerIntermediateCatchEvent("sub_timerB",
            new TimerDefinition(TimerType.Duration, "PT2H"));
        var sub_endA = new EndEvent("sub_endA");
        var sub_endB = new EndEvent("sub_endB");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [sub_start, ebg, timerA, timerB, sub_endA, sub_endB],
            SequenceFlows =
            [
                new SequenceFlow("sf1", sub_start, ebg),
                new SequenceFlow("sf2", ebg, timerA),
                new SequenceFlow("sf3", ebg, timerB),
                new SequenceFlow("sf4", timerA, sub_endA),
                new SequenceFlow("sf5", timerB, sub_endB)
            ]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-ebg-timer-wins",
            Activities = [start, subProcess, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, subProcess),
                new SequenceFlow("f2", subProcess, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted);
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "sub_timerA"),
            "TimerA should be active inside sub-process");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "sub_timerB"),
            "TimerB should be active inside sub-process");

        // Act — timerA fires (wins the race)
        var timerAEntry = snapshot.ActiveActivities.First(a => a.ActivityId == "sub_timerA");
        await workflowInstance.HandleTimerFired("sub_timerA", timerAEntry.ActivityInstanceId);

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should complete after timerA");

        // TimerB sibling inside sub-process should be cancelled
        var timerBActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "sub_timerB");
        Assert.IsNotNull(timerBActivity, "TimerB should be in completed list");
        Assert.IsTrue(timerBActivity.IsCancelled,
            "TimerB should be cancelled when sibling timerA wins inside sub-process");

        // TimerA should NOT be cancelled
        var timerAActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "sub_timerA");
        Assert.IsNotNull(timerAActivity, "TimerA should be in completed list");
        Assert.IsFalse(timerAActivity.IsCancelled, "TimerA should NOT be cancelled");

        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "End event should be reached");
    }

    [TestMethod]
    public async Task SubProcess_BoundaryTimerOnChildTask_CompletesNormally_ShouldNotInterruptWorkflow()
    {
        // Arrange: start -> subProcess(sub_start -> sub_task[+bt1(PT1H)] -> sub_end) -> end
        // The boundary timer is on a task *inside* the sub-process.
        // When the task completes normally, the timer should be unregistered (scope-aware lookup).
        // If not unregistered, a stale timer fire would be a no-op but the timer grain keeps firing.
        var sub_start = new StartEvent("sub_start");
        var sub_task = new TaskActivity("sub_task");
        var sub_end = new EndEvent("sub_end");
        var boundaryTimer = new BoundaryTimerEvent("sub_bt1", "sub_task",
            new TimerDefinition(TimerType.Duration, "PT1H"));
        var sub_endBoundary = new EndEvent("sub_endBoundary");

        var subProcess = new SubProcess("sub1")
        {
            Activities = [sub_start, sub_task, boundaryTimer, sub_end, sub_endBoundary],
            SequenceFlows =
            [
                new SequenceFlow("sf1", sub_start, sub_task),
                new SequenceFlow("sf2", sub_task, sub_end),
                new SequenceFlow("sf3", boundaryTimer, sub_endBoundary)
            ]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-child-task-boundary-timer",
            Activities = [start, subProcess, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, subProcess),
                new SequenceFlow("f2", subProcess, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted);
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "sub_task"));

        // Record the sub_task instance ID for boundary timer simulation
        var subTaskEntry = snapshot.ActiveActivities.First(a => a.ActivityId == "sub_task");

        // Act — complete sub_task normally (not via boundary timer)
        await workflowInstance.CompleteActivity("sub_task", new ExpandoObject());

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should complete via normal path");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Normal end should be reached");
        Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sub_endBoundary"),
            "Boundary path should NOT be taken");

        // Simulate stale boundary timer firing — it should be a no-op (activity already completed)
        await workflowInstance.HandleTimerFired("sub_bt1", subTaskEntry.ActivityInstanceId);

        var afterStaleSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(afterStaleSnapshot!.IsCompleted, "Workflow should still be completed after stale timer");
        Assert.IsFalse(afterStaleSnapshot.CompletedActivities.Any(a => a.ActivityId == "sub_endBoundary"),
            "Boundary path should NOT be taken even after stale timer fire");
    }
}
