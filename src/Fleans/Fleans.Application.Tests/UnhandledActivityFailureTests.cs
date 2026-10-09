using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Regression coverage for #762: an unhandled activity failure that consumes the last live
/// token must terminate the instance visibly (IsCompleted + IsFailed) instead of leaving it
/// with no active activities while neither completed nor failed.
/// </summary>
[TestClass]
public class UnhandledActivityFailureTests : WorkflowTestBase
{
    [TestMethod]
    public async Task UpstreamScriptFailure_BeforeCustomTask_FailsInstanceVisibly()
    {
        // Arrange — mirrors tests/manual/45-fleans-namespace: start → seed (script) → ct1
        // (custom task) → end. The seed script throws (the "FAIL" marker), so ct1 is never
        // reached. Before #762 the instance ended up Active=[], not completed, not failed.
        var start = new StartEvent("start");
        var seed = new ScriptTask("seed", "FAIL");
        var ct1 = new CustomTaskActivity("ct1", "stub-task", null, null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "unhandled-upstream-failure",
            Activities = [start, seed, ct1, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, seed),
                new SequenceFlow("f2", seed, ct1),
                new SequenceFlow("f3", ct1, end)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);

        // Act
        await instance.StartWorkflow();

        // Assert — the instance reaches a terminal, failed state.
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsFailed, "Instance must be marked failed after an unhandled failure consumed its last token.");
        Assert.IsFalse(snapshot.IsCancelled);
        Assert.IsNotNull(snapshot.CompletedAt);
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);

        var seedEntry = snapshot.CompletedActivities.Single(a => a.ActivityId == "seed");
        Assert.IsNotNull(seedEntry.ErrorState, "The failing activity must carry its error.");
        Assert.AreEqual("500", seedEntry.ErrorState.Code);
        Assert.AreEqual("Simulated script failure", seedEntry.ErrorState.Message);

        Assert.IsFalse(snapshot.CompletedActivityIds.Contains("ct1"), "ct1 must not run after the upstream failure.");
        Assert.IsFalse(snapshot.CompletedActivityIds.Contains("end"));
    }

    [TestMethod]
    public async Task CustomTask_FailedWithoutHandler_FailsInstance()
    {
        // Arrange
        var instance = await StartCustomTaskWorkflow("custom-task-unhandled-failure");

        // Act — plugin reports a failure with a custom error code; no boundary catches it.
        await instance.FailActivity("ct1", new CustomTaskFailedActivityException("E_STUB", "stub exploded"));

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "A failed instance is terminal.");
        Assert.IsTrue(snapshot.IsFailed);
        Assert.IsFalse(snapshot.IsCancelled);
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);

        var ct1 = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");
        Assert.IsNotNull(ct1.ErrorState);
        Assert.AreEqual("E_STUB", ct1.ErrorState.Code);
        Assert.AreEqual("stub exploded", ct1.ErrorState.Message);
        Assert.IsFalse(snapshot.CompletedActivityIds.Contains("end"));

        // A late duplicate failure (stream redelivery, keyed by activity instance) must not
        // change the terminal state.
        await instance.FailActivity("ct1", ct1.ActivityInstanceId, new CustomTaskFailedActivityException("E_STUB", "stub exploded"));
        var after = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsTrue(after!.IsFailed);
        Assert.AreEqual(1, after.CompletedActivities.Count(a => a.ActivityId == "ct1"));
    }

    [TestMethod]
    public async Task CustomTask_Completed_CompletesInstanceWithoutFailure()
    {
        // Arrange
        var instance = await StartCustomTaskWorkflow("custom-task-completed");

        // Act
        await instance.CompleteActivity("ct1", new ExpandoObject());

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed, "A successfully completed instance must not be marked failed.");
        Assert.IsFalse(snapshot.IsCancelled);
        Assert.IsNull(snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1").ErrorState);
        Assert.IsTrue(snapshot.CompletedActivityIds.Contains("end"));
    }

    [TestMethod]
    public async Task FailureCaughtByErrorBoundary_DoesNotFailInstance()
    {
        // Arrange — start → task1 → end, with a catch-all error boundary → recovered.
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = new BoundaryErrorEvent("boundary1", "task1", null);
        var end = new EndEvent("end");
        var recovered = new EndEvent("recovered");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "handled-failure",
            Activities = [start, task, boundary, end, recovered],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, recovered)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        // Act
        await instance.FailActivity("task1", new Exception("handled"));

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Error boundary path should complete the instance.");
        Assert.IsFalse(snapshot.IsFailed, "A failure caught by a boundary must not fail the instance.");
        Assert.IsTrue(snapshot.CompletedActivityIds.Contains("recovered"));
    }

    [TestMethod]
    public async Task UnhandledFailure_WhileAnotherBranchIsActive_DoesNotFailInstanceYet()
    {
        // Arrange — start → fork → (taskA → endA | taskB → endB)
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var taskA = new TaskActivity("taskA");
        var taskB = new TaskActivity("taskB");
        var endA = new EndEvent("endA");
        var endB = new EndEvent("endB");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "parallel-partial-failure",
            Activities = [start, fork, taskA, taskB, endA, endB],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                new SequenceFlow("f2", fork, taskA),
                new SequenceFlow("f3", fork, taskB),
                new SequenceFlow("f4", taskA, endA),
                new SequenceFlow("f5", taskB, endB)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        // Act
        await instance.FailActivity("taskA", new Exception("branch A failed"));

        // Assert — taskB still holds a live token, so the instance keeps running.
        var snapshot = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        CollectionAssert.Contains(snapshot.ActiveActivityIds, "taskB");
        Assert.IsNotNull(snapshot.CompletedActivities.Single(a => a.ActivityId == "taskA").ErrorState);
    }

    private async Task<IWorkflowInstanceGrain> StartCustomTaskWorkflow(string workflowId)
    {
        var start = new StartEvent("start");
        var ct1 = new CustomTaskActivity("ct1", "stub-task", null, null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, ct1, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, ct1),
                new SequenceFlow("f2", ct1, end)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        var waiting = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        CollectionAssert.Contains(waiting!.ActiveActivityIds, "ct1");
        Assert.IsFalse(waiting.IsFailed);
        return instance;
    }
}
