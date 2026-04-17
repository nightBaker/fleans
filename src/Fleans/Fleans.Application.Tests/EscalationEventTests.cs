using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EscalationEventTests : WorkflowTestBase
{
    // ── Interrupting Boundary ──────────────────────────────────────────

    [TestMethod]
    public async Task EscalationThrow_InterruptingBoundary_ShouldCancelHostAndFollowBoundaryPath()
    {
        // Arrange: start → sub(sub_start → task → escalationThrow → sub_end) → normalEnd
        //          + EscalationBoundaryEvent on sub (interrupting) → recoveryTask → boundaryEnd
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationThrow = new EscalationIntermediateThrowEvent("escalation_throw", "ESC-001");
        var subEnd = new EndEvent("sub_end");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationThrow, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationThrow),
                new SequenceFlow("sf3", escalationThrow, subEnd)
            ]
        };

        var start = new StartEvent("start");
        var boundary = new EscalationBoundaryEvent("esc_boundary", "sub", "ESC-001", IsInterrupting: true);
        var recoveryTask = new TaskActivity("recovery_task");
        var boundaryEnd = new EndEvent("boundary_end");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-interrupt",
            Activities = [start, sub, boundary, recoveryTask, boundaryEnd, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd),
                new SequenceFlow("f3", boundary, recoveryTask),
                new SequenceFlow("f4", recoveryTask, boundaryEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act: complete task → triggers escalation throw → interrupting boundary fires
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        // Wait for recovery_task to become active (boundary path)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "recovery_task"));

        // Assert: recovery_task is active, normal path not taken
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "recovery_task"));

        // Complete recovery task to finish workflow
        await workflowInstance.CompleteActivity("recovery_task", new ExpandoObject());

        var finalSnapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "boundary_end"),
            "Should complete via boundary path");
        Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "normal_end"),
            "Should NOT follow normal path");

        // Verify host subprocess was cancelled
        var subEntry = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "sub");
        Assert.IsNotNull(subEntry, "SubProcess should be in completed list");
        Assert.IsTrue(subEntry.IsCancelled, "SubProcess should be cancelled by interrupting boundary");
    }

    // ── Non-Interrupting Boundary ──────────────────────────────────────

    [TestMethod]
    public async Task EscalationThrow_NonInterruptingBoundary_ShouldFireBoundaryAndContinueHost()
    {
        // Arrange: start → sub(sub_start → task → escalationThrow → afterTask → sub_end) → normalEnd
        //          + EscalationBoundaryEvent on sub (non-interrupting) → boundaryEnd
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationThrow = new EscalationIntermediateThrowEvent("escalation_throw", "ESC-002");
        var afterTask = new TaskActivity("after_task");
        var subEnd = new EndEvent("sub_end");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationThrow, afterTask, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationThrow),
                new SequenceFlow("sf3", escalationThrow, afterTask),
                new SequenceFlow("sf4", afterTask, subEnd)
            ]
        };

        var start = new StartEvent("start");
        var boundary = new EscalationBoundaryEvent("esc_boundary", "sub", "ESC-002", IsInterrupting: false);
        var boundaryEnd = new EndEvent("boundary_end");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-non-interrupt",
            Activities = [start, sub, boundary, boundaryEnd, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd),
                new SequenceFlow("f3", boundary, boundaryEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act: complete task → triggers escalation throw → non-interrupting boundary fires
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        // Wait for after_task to become active (normal path continues)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "after_task"));

        // Assert: after_task is active (host continues), boundary path already completed
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "after_task"),
            "Host activity should continue after non-interrupting boundary");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "boundary_end"),
            "Boundary path should have completed");

        // Complete after_task to finish the subprocess and workflow
        await workflowInstance.CompleteActivity("after_task", new ExpandoObject());

        var finalSnapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "normal_end"),
            "Normal path should complete");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "boundary_end"),
            "Boundary path should also complete");
    }

    // ── Uncaught Escalation ────────────────────────────────────────────

    [TestMethod]
    public async Task EscalationThrow_NoBoundary_ShouldCompleteNormally()
    {
        // BPMN spec: uncaught escalation is non-faulting — workflow continues normally
        // Arrange: start → sub(sub_start → task → escalationThrow → afterTask → sub_end) → normalEnd
        //          No boundary event on sub
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationThrow = new EscalationIntermediateThrowEvent("escalation_throw", "ESC-003");
        var afterTask = new TaskActivity("after_task");
        var subEnd = new EndEvent("sub_end");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationThrow, afterTask, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationThrow),
                new SequenceFlow("sf3", escalationThrow, afterTask),
                new SequenceFlow("sf4", afterTask, subEnd)
            ]
        };

        var start = new StartEvent("start");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-uncaught",
            Activities = [start, sub, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act: complete task → triggers uncaught escalation → continues normally
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        // after_task should become active (escalation is non-faulting)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "after_task"));
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "after_task"));

        await workflowInstance.CompleteActivity("after_task", new ExpandoObject());

        var finalSnapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "normal_end"));
    }

    // ── Catch-All Boundary ─────────────────────────────────────────────

    [TestMethod]
    public async Task EscalationThrow_CatchAllBoundary_ShouldCatchAnyCode()
    {
        // Arrange: escalation boundary with null code (catch-all)
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationThrow = new EscalationIntermediateThrowEvent("escalation_throw", "SOME-RANDOM-CODE");
        var subEnd = new EndEvent("sub_end");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationThrow, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationThrow),
                new SequenceFlow("sf3", escalationThrow, subEnd)
            ]
        };

        var start = new StartEvent("start");
        var boundary = new EscalationBoundaryEvent("esc_boundary", "sub", null, IsInterrupting: true);
        var recoveryEnd = new EndEvent("recovery_end");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-catch-all",
            Activities = [start, sub, boundary, recoveryEnd, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd),
                new SequenceFlow("f3", boundary, recoveryEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "recovery_end"),
            "Catch-all boundary should catch the escalation");
        Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "normal_end"),
            "Should NOT follow normal path");
    }

    // ── Variable Merging ───────────────────────────────────────────────

    [TestMethod]
    public async Task EscalationThrow_InterruptingBoundary_ShouldCloneVariablesFromScope()
    {
        // Verify that escalation boundary receives variables from the scope where escalation was thrown
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationThrow = new EscalationIntermediateThrowEvent("escalation_throw", "ESC-VAR");
        var subEnd = new EndEvent("sub_end");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationThrow, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationThrow),
                new SequenceFlow("sf3", escalationThrow, subEnd)
            ]
        };

        var start = new StartEvent("start");
        var boundary = new EscalationBoundaryEvent("esc_boundary", "sub", "ESC-VAR", IsInterrupting: true);
        var checkTask = new TaskActivity("check_task");
        var boundaryEnd = new EndEvent("boundary_end");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-vars",
            Activities = [start, sub, boundary, checkTask, boundaryEnd, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd),
                new SequenceFlow("f3", boundary, checkTask),
                new SequenceFlow("f4", checkTask, boundaryEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task with a variable — should be available in boundary scope
        dynamic vars = new ExpandoObject();
        vars.taskResult = "escalated-value";
        await workflowInstance.CompleteActivity("task", vars);

        // Wait for check_task (boundary path)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "check_task"));

        var checkTaskSnapshot = snapshot.ActiveActivities.First(a => a.ActivityId == "check_task");
        var result = await workflowInstance.GetVariable(checkTaskSnapshot.VariablesStateId, "taskResult");
        Assert.AreEqual("escalated-value", result,
            "Boundary path should have access to variables from the scope where escalation was thrown");

        await workflowInstance.CompleteActivity("check_task", new ExpandoObject());
        var finalSnapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
    }

    // ── EscalationEndEvent ─────────────────────────────────────────────

    [TestMethod]
    public async Task EscalationEndEvent_InSubProcess_ShouldThrowEscalationAndComplete()
    {
        // EscalationEndEvent: throws escalation AND completes the subprocess
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationEnd = new EscalationEndEvent("escalation_end", "ESC-END");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationEnd)
            ]
        };

        var start = new StartEvent("start");
        var boundary = new EscalationBoundaryEvent("esc_boundary", "sub", "ESC-END", IsInterrupting: true);
        var recoveryEnd = new EndEvent("recovery_end");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-end-event",
            Activities = [start, sub, boundary, recoveryEnd, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd),
                new SequenceFlow("f3", boundary, recoveryEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "recovery_end"),
            "Escalation end event should trigger boundary path");
    }

    // ── Completed Activities List ──────────────────────────────────────

    [TestMethod]
    public async Task EscalationThrow_InterruptingBoundary_ShouldHaveCorrectCompletedActivities()
    {
        // Verify the exact set of completed activities after interrupting escalation
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationThrow = new EscalationIntermediateThrowEvent("escalation_throw", "ESC-005");
        var subEnd = new EndEvent("sub_end");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationThrow, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationThrow),
                new SequenceFlow("sf3", escalationThrow, subEnd)
            ]
        };

        var start = new StartEvent("start");
        var boundary = new EscalationBoundaryEvent("esc_boundary", "sub", "ESC-005", IsInterrupting: true);
        var boundaryEnd = new EndEvent("boundary_end");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-completed-list",
            Activities = [start, sub, boundary, boundaryEnd, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd),
                new SequenceFlow("f3", boundary, boundaryEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);

        // start, sub_start, task, escalation_throw should be completed
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "start"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sub_start"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "task"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "escalation_throw"));
        // boundary path
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "esc_boundary"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "boundary_end"));
        // sub should be cancelled, not completed normally
        var subEntry = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "sub");
        Assert.IsTrue(subEntry.IsCancelled);
        // No active activities
        Assert.AreEqual(0, finalSnapshot.ActiveActivities.Count);
    }

    // ── Failure Paths ──────────────────────────────────────────────────

    [TestMethod]
    public async Task EscalationActivity_Failure_ShouldSetErrorState()
    {
        // When an activity before escalation fails, verify error state is set correctly
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationThrow = new EscalationIntermediateThrowEvent("escalation_throw", "ESC-FAIL");
        var subEnd = new EndEvent("sub_end");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationThrow, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationThrow),
                new SequenceFlow("sf3", escalationThrow, subEnd)
            ]
        };

        var start = new StartEvent("start");
        var boundary = new EscalationBoundaryEvent("esc_boundary", "sub", "ESC-FAIL", IsInterrupting: true);
        var boundaryEnd = new EndEvent("boundary_end");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-failure",
            Activities = [start, sub, boundary, boundaryEnd, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd),
                new SequenceFlow("f3", boundary, boundaryEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Fail the task instead of completing it → no escalation thrown, error state set
        await workflowInstance.FailActivity("task", new Exception("Task failed before escalation"));

        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);

        // Workflow should NOT complete (failed task without error boundary handler on task itself)
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should not complete when task fails");

        var failedEntry = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task");
        Assert.IsNotNull(failedEntry, "Failed task should be in completed list");
        Assert.IsNotNull(failedEntry.ErrorState, "Failed task should have error state");
        Assert.AreEqual(500, failedEntry.ErrorState.Code, "Generic exception should produce 500 error code");
        Assert.AreEqual("Task failed before escalation", failedEntry.ErrorState.Message);
    }

    [TestMethod]
    public async Task EscalationActivity_BadRequestFailure_ShouldSet400ErrorCode()
    {
        // Verify BadRequestActivityException produces 400 error code
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var subEnd = new EndEvent("sub_end");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, subEnd)
            ]
        };

        var start = new StartEvent("start");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-bad-request",
            Activities = [start, sub, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        await workflowInstance.FailActivity("task",
            new Fleans.Domain.Errors.BadRequestActivityException("Invalid input"));

        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);

        var failedEntry = snapshot!.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task");
        Assert.IsNotNull(failedEntry);
        Assert.IsNotNull(failedEntry.ErrorState);
        Assert.AreEqual(400, failedEntry.ErrorState.Code, "BadRequestActivityException should produce 400 error code");
    }

    // ── EscalationEndEvent in SubProcess without boundary ─────────────

    [TestMethod]
    public async Task EscalationEndEvent_InSubProcess_NoBoundary_ShouldCompleteNormally()
    {
        // B1 regression test: EscalationEndEvent inside a SubProcess without a boundary handler.
        // The escalation is uncaught (non-faulting per BPMN spec) and the subprocess should
        // complete normally. Previously, EscalationEndEvent always emitted CompleteWorkflowCommand
        // which would prematurely terminate the root workflow.
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationEnd = new EscalationEndEvent("escalation_end", "ESC-UNCAUGHT");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationEnd)
            ]
        };

        var start = new StartEvent("start");
        var afterSub = new TaskActivity("after_sub");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-end-no-boundary",
            Activities = [start, sub, afterSub, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, afterSub),
                new SequenceFlow("f3", afterSub, normalEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task → EscalationEndEvent fires → uncaught, subprocess completes → after_sub becomes active
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "after_sub"));

        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "after_sub"),
            "Root workflow should continue after subprocess with uncaught EscalationEndEvent");

        // Complete after_sub to finish workflow
        await workflowInstance.CompleteActivity("after_sub", new ExpandoObject());

        var finalSnapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsFalse(finalSnapshot.IsCancelled, "Workflow should be completed, not cancelled");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "normal_end"),
            "Should follow normal path to end");
    }

    // ── EscalationEndEvent with non-interrupting boundary ─────────────

    [TestMethod]
    public async Task EscalationEndEvent_InSubProcess_NonInterruptingBoundary_ShouldComplete()
    {
        // EscalationEndEvent inside a SubProcess with a non-interrupting boundary.
        // Both the boundary path and the normal path should complete.
        var subStart = new StartEvent("sub_start");
        var task = new TaskActivity("task");
        var escalationEnd = new EscalationEndEvent("escalation_end", "ESC-NI");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, task, escalationEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, task),
                new SequenceFlow("sf2", task, escalationEnd)
            ]
        };

        var start = new StartEvent("start");
        var boundary = new EscalationBoundaryEvent("esc_boundary", "sub", "ESC-NI", IsInterrupting: false);
        var boundaryEnd = new EndEvent("boundary_end");
        var normalEnd = new EndEvent("normal_end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esc-end-non-interrupt",
            Activities = [start, sub, boundary, boundaryEnd, normalEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, normalEnd),
                new SequenceFlow("f3", boundary, boundaryEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "boundary_end"),
            "Boundary path should complete");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "normal_end"),
            "Normal path should also complete after subprocess ends");
    }
}
