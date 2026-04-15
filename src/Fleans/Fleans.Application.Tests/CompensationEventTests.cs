using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class CompensationEventTests : WorkflowTestBase
{
    // ---------- Helpers ----------

    /// <summary>
    /// Creates a ScriptTask that auto-completes (no "FAIL" script).
    /// </summary>
    private static ScriptTask AutoTask(string id) => new(id, "PASS", "csharp");

    /// <summary>
    /// Builds the root-scope workflow fixture used by most tests:
    ///   start → task_a (ScriptTask) → task_b (ScriptTask) → throw_comp → end
    /// With compensation boundaries:
    ///   cb_a on task_a → handler_a (ScriptTask)
    ///   cb_b on task_b → handler_b (ScriptTask)
    /// </summary>
    private static WorkflowDefinition BuildTwoTaskWorkflow(
        string? throwTargetRef = null,
        bool useCompensationEndEvent = false)
    {
        var start = new StartEvent("start");
        var taskA = AutoTask("task_a");
        var taskB = AutoTask("task_b");

        var cbA = new CompensationBoundaryEvent("cb_a", "task_a", "handler_a");
        var cbB = new CompensationBoundaryEvent("cb_b", "task_b", "handler_b");
        var handlerA = AutoTask("handler_a");
        var handlerB = AutoTask("handler_b");

        var activities = new List<Activity> { start, taskA, taskB, cbA, cbB, handlerA, handlerB };
        var flows = new List<SequenceFlow>
        {
            new("f1", start, taskA),
            new("f2", taskA, taskB)
        };

        if (useCompensationEndEvent)
        {
            var compEnd = new CompensationEndEvent("comp_end");
            activities.Add(compEnd);
            flows.Add(new SequenceFlow("f3", taskB, compEnd));
        }
        else
        {
            var throwComp = new CompensationIntermediateThrowEvent("throw_comp", throwTargetRef);
            var end = new EndEvent("end");
            activities.Add(throwComp);
            activities.Add(end);
            flows.Add(new SequenceFlow("f3", taskB, throwComp));
            flows.Add(new SequenceFlow("f4", throwComp, end));
        }

        return new WorkflowDefinition
        {
            WorkflowId = "compensation-test",
            Activities = activities,
            SequenceFlows = flows
        };
    }

    // ---------- Tests ----------

    [TestMethod]
    public async Task SingleHandler_CompensationWalk_ShouldRunHandlerAndComplete()
    {
        // Arrange: start → task_a → throw_comp → end
        // cb_a on task_a → handler_a
        // Only task_a has a compensation boundary
        var start = new StartEvent("start");
        var taskA = AutoTask("task_a");
        var cbA = new CompensationBoundaryEvent("cb_a", "task_a", "handler_a");
        var handlerA = AutoTask("handler_a");
        var throwComp = new CompensationIntermediateThrowEvent("throw_comp", null);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "comp-single",
            Activities = [start, taskA, cbA, handlerA, throwComp, end],
            SequenceFlows =
            [
                new("f1", start, taskA),
                new("f2", taskA, throwComp),
                new("f3", throwComp, end)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        // Act & Assert: workflow should complete with handler executed
        var snapshot = await WaitForCondition(
            grain.GetPrimaryKey(),
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted, "Workflow should complete after compensation walk");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "task_a"),
            "task_a should be completed");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_a"),
            "handler_a should have been executed by the compensation walk");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "end should be completed after compensation walk finishes");
    }

    [TestMethod]
    public async Task MultipleHandlers_CompensationWalk_ShouldExecuteInReverseCompletionOrder()
    {
        // Arrange: start → task_a → task_b → throw → end
        // Compensation walk should run handler_b first (task_b completed last), then handler_a
        var workflow = BuildTwoTaskWorkflow();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        // Assert: both handlers ran and workflow completed
        var snapshot = await WaitForCondition(
            grain.GetPrimaryKey(),
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_a"),
            "handler_a should have run");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_b"),
            "handler_b should have run");

        // Verify reverse order: handler_b completed before handler_a
        var handlerACompleted = snapshot.CompletedActivities
            .First(a => a.ActivityId == "handler_a").CompletedAt;
        var handlerBCompleted = snapshot.CompletedActivities
            .First(a => a.ActivityId == "handler_b").CompletedAt;
        Assert.IsTrue(handlerBCompleted <= handlerACompleted,
            "handler_b (for task_b completed later) should run before handler_a");
    }

    [TestMethod]
    public async Task TargetedCompensation_ShouldOnlyRunNamedHandler()
    {
        // Arrange: throw targets only "task_a" — handler_b should NOT run
        var workflow = BuildTwoTaskWorkflow(throwTargetRef: "task_a");
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        var snapshot = await WaitForCondition(
            grain.GetPrimaryKey(),
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_a"),
            "handler_a should run (task_a was targeted)");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_b"),
            "handler_b should NOT run (task_b was not targeted)");
    }

    [TestMethod]
    public async Task ActivityWithoutCompensationBoundary_ShouldBeSkippedInWalk()
    {
        // Arrange: only task_a has a handler; task_b does not
        var start = new StartEvent("start");
        var taskA = AutoTask("task_a");
        var taskB = AutoTask("task_b");           // no compensation boundary
        var cbA = new CompensationBoundaryEvent("cb_a", "task_a", "handler_a");
        var handlerA = AutoTask("handler_a");
        var throwComp = new CompensationIntermediateThrowEvent("throw_comp", null);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "comp-skip",
            Activities = [start, taskA, taskB, cbA, handlerA, throwComp, end],
            SequenceFlows =
            [
                new("f1", start, taskA),
                new("f2", taskA, taskB),
                new("f3", taskB, throwComp),
                new("f4", throwComp, end)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        var snapshot = await WaitForCondition(
            grain.GetPrimaryKey(),
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_a"),
            "handler_a should run for task_a");
        // task_b had no boundary — skipped without error, workflow still completes
    }

    [TestMethod]
    public async Task CompensationEndEvent_ShouldTriggerCompensationAndEndScope()
    {
        // Arrange: start → task_a → comp_end (no outgoing flow)
        var workflow = BuildTwoTaskWorkflow(useCompensationEndEvent: true);
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        var snapshot = await WaitForCondition(
            grain.GetPrimaryKey(),
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted, "Workflow should complete via CompensationEndEvent");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_a"),
            "handler_a should run");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_b"),
            "handler_b should run");
    }

    [TestMethod]
    public async Task NoCompensableActivities_WalkCompletesImmediately_WorkflowCompletes()
    {
        // Arrange: no activities with compensation boundaries
        var start = new StartEvent("start");
        var taskA = AutoTask("task_a");
        var throwComp = new CompensationIntermediateThrowEvent("throw_comp", null);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "comp-empty",
            Activities = [start, taskA, throwComp, end],
            SequenceFlows =
            [
                new("f1", start, taskA),
                new("f2", taskA, throwComp),
                new("f3", throwComp, end)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        var snapshot = await WaitForCondition(
            grain.GetPrimaryKey(),
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted, "Workflow should complete even with empty compensation walk");
    }

    [TestMethod]
    public async Task CompensationSnapshotIsolation_HandlerReceivesVariablesAtCompletionTime()
    {
        // Arrange: task_a sets variable, then workflow overwrites it, compensation handler should see original value
        // We use TaskActivity for task_a (manual completion) to control variable values
        var start = new StartEvent("start");
        var taskA = new TaskActivity("task_a");      // manual completion; we set variables explicitly
        var taskB = AutoTask("task_b");
        var cbA = new CompensationBoundaryEvent("cb_a", "task_a", "handler_a");
        var handlerA = new TaskActivity("handler_a"); // manual — we read its variables
        var throwComp = new CompensationIntermediateThrowEvent("throw_comp", null);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "comp-snapshot",
            Activities = [start, taskA, taskB, cbA, handlerA, throwComp, end],
            SequenceFlows =
            [
                new("f1", start, taskA),
                new("f2", taskA, taskB),
                new("f3", taskB, throwComp),
                new("f4", throwComp, end)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        var instanceId = grain.GetPrimaryKey();

        // Complete task_a with a specific variable value
        dynamic taskAVars = new ExpandoObject();
        taskAVars.compensationValue = "original";
        await grain.CompleteActivity("task_a", taskAVars);

        // task_b auto-completes; throw fires compensation; handler_a should be active
        var handlerActiveSnapshot = await WaitForCondition(
            instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "handler_a"),
            timeoutMs: 5000);

        var handlerEntry = handlerActiveSnapshot.ActiveActivities.First(a => a.ActivityId == "handler_a");

        // Read the handler's variables — should contain the snapshot from when task_a completed
        var value = await grain.GetVariable(handlerEntry.VariablesStateId, "compensationValue");
        Assert.AreEqual("original", value?.ToString(),
            "Compensation handler should have the variable snapshot from task_a's completion time");

        // Complete the handler and end
        await grain.CompleteActivity("handler_a", new ExpandoObject());

        var finalSnapshot = await WaitForCondition(
            instanceId,
            s => s.IsCompleted,
            timeoutMs: 5000);

        Assert.IsTrue(finalSnapshot.IsCompleted);
    }
}
