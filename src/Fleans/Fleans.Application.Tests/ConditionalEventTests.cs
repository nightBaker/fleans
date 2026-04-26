using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Integration tests for ConditionalBoundaryEvent.
///
/// Key design note: conditional watchers are evaluated in RunExecutionLoop only when
/// newlyCompletedEntryIds.Count > 0 (line 120). Watchers for a boundary are registered
/// when the HOST activity executes. To trigger evaluation while the host is still active,
/// a DIFFERENT activity must complete in the same execution loop. This is achieved by
/// using a ParallelGateway fork into two parallel tasks: task1 (with boundary) and task2
/// (trigger). Completing task2 externally triggers watcher evaluation while task1 is still
/// active. Note: StartEvent only follows a single outgoing flow, so a ParallelGateway is
/// required for parallel paths.
/// </summary>
[TestClass]
public class ConditionalBoundaryEventTests : WorkflowTestBase
{
    /// <summary>
    /// Interrupting boundary fires when condition becomes true after another activity completes.
    /// Workflow: start → fork → task1 (with conditional boundary "true") → end1
    ///                       → task2 (trigger)                          → end2
    ///                         boundary → recovery → boundaryEnd
    ///
    /// Completing task2 triggers watcher evaluation. Condition "true" → boundary interrupts task1.
    /// </summary>
    [TestMethod]
    public async Task InterruptingBoundary_ConditionTrue_ShouldInterruptHostAndFollowBoundaryPath()
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var boundary = new ConditionalBoundaryEvent("boundary1", "task1", "true", IsInterrupting: true);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var recovery = new TaskActivity("recovery");
        var boundaryEnd = new EndEvent("boundaryEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-boundary-interrupting",
            Activities = [start, fork, task1, task2, boundary, end1, end2, recovery, boundaryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f0", start, fork),
                new SequenceFlow("f1", fork, task1),
                new SequenceFlow("f2", fork, task2),
                new SequenceFlow("f3", task1, end1),
                new SequenceFlow("f4", task2, end2),
                new SequenceFlow("f5", boundary, recovery),
                new SequenceFlow("f6", recovery, boundaryEnd)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        var instanceId = instance.GetPrimaryKey();

        // Verify both tasks are active
        await WaitForCondition(instanceId, s =>
            s.ActiveActivities.Any(a => a.ActivityId == "task1") &&
            s.ActiveActivities.Any(a => a.ActivityId == "task2"));

        // Complete task2 — triggers watcher evaluation while task1 is still active
        await instance.CompleteActivity("task2", new ExpandoObject());

        // Boundary should fire, interrupting task1. Recovery should be active.
        var snapshot = await WaitForCondition(instanceId, s =>
            s.ActiveActivities.Any(a => a.ActivityId == "recovery"));

        Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed yet — recovery pending");
        var interruptedTask = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(interruptedTask, "task1 should be completed (interrupted)");
        Assert.IsTrue(interruptedTask.IsCancelled, "task1 should be cancelled by interrupting boundary");

        // Complete recovery to finish the workflow
        await instance.CompleteActivity("recovery", new ExpandoObject());

        var finalSnapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(finalSnapshot.IsCompleted, "Workflow should be completed after recovery");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "boundaryEnd"),
            "Should complete via boundary end");
    }

    /// <summary>
    /// Task completes before condition can fire — boundary should NOT fire.
    /// Uses condition "false" so watcher never fires.
    /// </summary>
    [TestMethod]
    public async Task InterruptingBoundary_ConditionFalse_TaskCompletesNormally()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = new ConditionalBoundaryEvent("boundary1", "task1", "false", IsInterrupting: true);
        var end = new EndEvent("end");
        var boundaryEnd = new EndEvent("boundaryEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-boundary-false",
            Activities = [start, task, boundary, end, boundaryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, boundaryEnd)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        // Complete task normally
        await instance.CompleteActivity("task1", new ExpandoObject());

        var instanceId = instance.GetPrimaryKey();
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed via normal flow");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should complete via normal end event");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "boundaryEnd"),
            "Should NOT complete via boundary end event");
    }

    /// <summary>
    /// Non-interrupting boundary fires when condition becomes true, but host continues.
    /// </summary>
    [TestMethod]
    public async Task NonInterruptingBoundary_ConditionTrue_HostContinuesAndBoundaryFires()
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var boundary = new ConditionalBoundaryEvent("boundary1", "task1", "true", IsInterrupting: false);
        var afterBoundary = new TaskActivity("afterBoundary");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var end3 = new EndEvent("end3");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-ni-boundary",
            Activities = [start, fork, task1, task2, boundary, afterBoundary, end1, end2, end3],
            SequenceFlows =
            [
                new SequenceFlow("f0", start, fork),
                new SequenceFlow("f1", fork, task1),
                new SequenceFlow("f2", fork, task2),
                new SequenceFlow("f3", task1, end1),
                new SequenceFlow("f4", task2, end2),
                new SequenceFlow("f5", boundary, afterBoundary),
                new SequenceFlow("f6", afterBoundary, end3)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        var instanceId = instance.GetPrimaryKey();

        // Wait for both tasks active
        await WaitForCondition(instanceId, s =>
            s.ActiveActivities.Any(a => a.ActivityId == "task1") &&
            s.ActiveActivities.Any(a => a.ActivityId == "task2"));

        // Complete task2 to trigger watcher evaluation
        await instance.CompleteActivity("task2", new ExpandoObject());

        // Wait for boundary to fire (afterBoundary should be active)
        var snapshot = await WaitForCondition(instanceId, s =>
            s.ActiveActivities.Any(a => a.ActivityId == "afterBoundary"));

        Assert.IsFalse(snapshot.IsCompleted, "Workflow should not be completed yet");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "task1 should still be active (non-interrupting)");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "task1" && a.IsCancelled),
            "task1 should NOT be cancelled");

        // Complete both remaining paths
        await instance.CompleteActivity("task1", new ExpandoObject());
        await instance.CompleteActivity("afterBoundary", new ExpandoObject());

        var finalSnapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(finalSnapshot.IsCompleted, "Workflow should be completed after all paths finish");
    }

    /// <summary>
    /// Verify completed activities list includes all expected activities.
    /// </summary>
    [TestMethod]
    public async Task InterruptingBoundary_CompletedActivitiesList_ShouldIncludeAllExpected()
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var boundary = new ConditionalBoundaryEvent("boundary1", "task1", "true", IsInterrupting: true);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var recovery = new ScriptTask("recovery", "noop", "csharp");
        var boundaryEnd = new EndEvent("boundaryEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-completed-list",
            Activities = [start, fork, task1, task2, boundary, end1, end2, recovery, boundaryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f0", start, fork),
                new SequenceFlow("f1", fork, task1),
                new SequenceFlow("f2", fork, task2),
                new SequenceFlow("f3", task1, end1),
                new SequenceFlow("f4", task2, end2),
                new SequenceFlow("f5", boundary, recovery),
                new SequenceFlow("f6", recovery, boundaryEnd)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        var instanceId = instance.GetPrimaryKey();

        // Wait for both active
        await WaitForCondition(instanceId, s =>
            s.ActiveActivities.Any(a => a.ActivityId == "task1") &&
            s.ActiveActivities.Any(a => a.ActivityId == "task2"));

        // Complete task2 to trigger boundary
        await instance.CompleteActivity("task2", new ExpandoObject());

        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsCompleted);
        var completedIds = snapshot.CompletedActivities.Select(a => a.ActivityId).ToHashSet();
        Assert.IsTrue(completedIds.Contains("start"), "start should be completed");
        Assert.IsTrue(completedIds.Contains("fork"), "fork should be completed");
        Assert.IsTrue(completedIds.Contains("task1"), "task1 should be completed (cancelled)");
        Assert.IsTrue(completedIds.Contains("task2"), "task2 should be completed");
        Assert.IsTrue(completedIds.Contains("recovery"), "recovery should be completed");
        Assert.IsTrue(completedIds.Contains("boundaryEnd"), "boundaryEnd should be completed");
    }

    /// <summary>
    /// Failure test: when host activity fails, error state should have correct 500 code.
    /// </summary>
    [TestMethod]
    public async Task ConditionalBoundary_HostActivityFails_ErrorStateCorrect()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = new ConditionalBoundaryEvent("boundary1", "task1", "false", IsInterrupting: true);
        var end = new EndEvent("end");
        var boundaryEnd = new EndEvent("boundaryEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-boundary-fail",
            Activities = [start, task, boundary, end, boundaryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, boundaryEnd)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        await instance.FailActivity("task1", new Exception("Something went wrong"));

        var instanceId = instance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);

        var failedEntry = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(failedEntry, "task1 should be in completed activities after failure");
        Assert.IsNotNull(failedEntry.ErrorState, "Failed task should have error state");
        Assert.AreEqual("500", failedEntry.ErrorState.Code, "Generic exception should produce 500 error code");
        Assert.AreEqual("Something went wrong", failedEntry.ErrorState.Message);
    }

    /// <summary>
    /// Failure test: BadRequestActivityException produces 400 error code.
    /// </summary>
    [TestMethod]
    public async Task ConditionalBoundary_HostActivityFailsBadRequest_Error400()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = new ConditionalBoundaryEvent("boundary1", "task1", "false", IsInterrupting: true);
        var end = new EndEvent("end");
        var boundaryEnd = new EndEvent("boundaryEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-boundary-fail-400",
            Activities = [start, task, boundary, end, boundaryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, boundaryEnd)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        await instance.FailActivity("task1", new BadRequestActivityException("Invalid input"));

        var instanceId = instance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);

        var failedEntry = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(failedEntry);
        Assert.IsNotNull(failedEntry.ErrorState);
        Assert.AreEqual("400", failedEntry.ErrorState.Code, "BadRequestActivityException should produce 400 error code");
    }
}

[TestClass]
public class ConditionalIntermediateCatchEventTests : WorkflowTestBase
{
    /// <summary>
    /// Intermediate catch event blocks until condition becomes true.
    /// The catch registers a watcher when it executes. A parallel task's completion
    /// triggers evaluation. With condition "true", the catch resumes.
    /// </summary>
    [TestMethod]
    public async Task IntermediateCatch_ConditionTrue_ShouldResumeFlow()
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var condCatch = new ConditionalIntermediateCatchEvent("condCatch", "true");
        var task2 = new TaskActivity("task2");
        var script = new ScriptTask("afterCatch", "noop", "csharp");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-catch-true",
            Activities = [start, fork, condCatch, task2, script, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("f0", start, fork),
                new SequenceFlow("f1", fork, condCatch),
                new SequenceFlow("f2", fork, task2),
                new SequenceFlow("f3", condCatch, script),
                new SequenceFlow("f4", script, end1),
                new SequenceFlow("f5", task2, end2)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        var instanceId = instance.GetPrimaryKey();

        // Wait for both to be active
        await WaitForCondition(instanceId, s =>
            s.ActiveActivities.Any(a => a.ActivityId == "condCatch") &&
            s.ActiveActivities.Any(a => a.ActivityId == "task2"));

        // Complete task2 to trigger watcher evaluation
        await instance.CompleteActivity("task2", new ExpandoObject());

        // Condition "true" should fire, completing condCatch → afterCatch → end1
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should complete — condition was true");

        var completedIds = snapshot.CompletedActivities.Select(a => a.ActivityId).ToHashSet();
        Assert.IsTrue(completedIds.Contains("condCatch"), "Conditional catch should be completed");
        Assert.IsTrue(completedIds.Contains("afterCatch"), "afterCatch should be completed after catch resumes");
    }

    /// <summary>
    /// Intermediate catch with condition "false" should block indefinitely.
    /// </summary>
    [TestMethod]
    public async Task IntermediateCatch_ConditionFalse_ShouldBlockFlow()
    {
        var start = new StartEvent("start");
        var condCatch = new ConditionalIntermediateCatchEvent("condCatch", "false");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-catch-false",
            Activities = [start, condCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, condCatch),
                new SequenceFlow("f2", condCatch, end)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        var instanceId = instance.GetPrimaryKey();
        // Give it a moment, then verify it's still waiting
        await Task.Delay(500);

        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed — condition is false");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "condCatch"),
            "Conditional catch should still be in active activities");
    }

    /// <summary>
    /// Verify all expected activities are in completed list.
    /// </summary>
    [TestMethod]
    public async Task IntermediateCatch_CompletedActivities_ShouldContainAllExpected()
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var condCatch = new ConditionalIntermediateCatchEvent("condCatch", "true");
        var task2 = new TaskActivity("task2");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-catch-completed",
            Activities = [start, fork, condCatch, task2, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("f0", start, fork),
                new SequenceFlow("f1", fork, condCatch),
                new SequenceFlow("f2", fork, task2),
                new SequenceFlow("f3", condCatch, end1),
                new SequenceFlow("f4", task2, end2)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        var instanceId = instance.GetPrimaryKey();

        await WaitForCondition(instanceId, s =>
            s.ActiveActivities.Any(a => a.ActivityId == "condCatch") &&
            s.ActiveActivities.Any(a => a.ActivityId == "task2"));

        await instance.CompleteActivity("task2", new ExpandoObject());

        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsCompleted);
        var completedIds = snapshot.CompletedActivities.Select(a => a.ActivityId).ToHashSet();
        Assert.IsTrue(completedIds.Contains("start"));
        Assert.IsTrue(completedIds.Contains("fork"));
        Assert.IsTrue(completedIds.Contains("condCatch"));
        Assert.IsTrue(completedIds.Contains("task2"));
        Assert.IsTrue(completedIds.Contains("end1"));
        Assert.IsTrue(completedIds.Contains("end2"));
        Assert.AreEqual(0, snapshot.ActiveActivities.Count, "No active activities should remain");
    }
}
