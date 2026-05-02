using Fleans.Application;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

[TestClass]
public class CompensationLogServiceTests : WorkflowTestBase
{
    private static ScriptTask AutoTask(string id) => new(id, "PASS", "csharp");

    /// <summary>
    /// Resolves the application service from the test silo. Mirrors how the admin UI
    /// page (ProcessInstance.razor) consumes the service via DI, ensuring the registration
    /// in <see cref="ApplicationDependencyInjection"/> is exercised by the test.
    /// </summary>
    private ICompensationLogService Service => GetSiloService<ICompensationLogService>();

    [TestMethod]
    public async Task EmptyCompensationLog_ReturnsEmptyList()
    {
        // Arrange: workflow that completes normally — no compensable activities, no walk.
        var start = new StartEvent("start");
        var taskA = AutoTask("task_a");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "no-compensation",
            Activities = [start, taskA, end],
            SequenceFlows =
            [
                new("f1", start, taskA),
                new("f2", taskA, end)
            ]
        };

        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        await WaitForCondition(instanceId, s => s.IsCompleted, timeoutMs: 5000);

        // Act
        var entries = await Service.GetCompensationLog(instanceId);

        // Assert
        Assert.AreEqual(0, entries.Count, "CompensationLog should be empty when no compensable activity ran");
    }

    [TestMethod]
    public async Task TwoEntriesRootScope_OrderedAscBySequence_BothCompensated_HandlersResolved()
    {
        // Arrange: root-scope compensation broadcast (mirrors compensation-broadcast.bpmn).
        //   start → task_a → task_b → throw_comp → end
        //   cb_a on task_a → handler_a;  cb_b on task_b → handler_b
        // After walk completes: handler_b ran first (reverse-completion), then handler_a;
        // both entries are marked IsCompensated=true.
        var start = new StartEvent("start");
        var taskA = AutoTask("task_a");
        var taskB = AutoTask("task_b");
        var cbA = new CompensationBoundaryEvent("cb_a", "task_a", "handler_a");
        var cbB = new CompensationBoundaryEvent("cb_b", "task_b", "handler_b");
        var handlerA = AutoTask("handler_a");
        var handlerB = AutoTask("handler_b");
        var throwComp = new CompensationIntermediateThrowEvent("throw_comp", null);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "compensation-log-test",
            Activities = [start, taskA, taskB, cbA, cbB, handlerA, handlerB, throwComp, end],
            SequenceFlows =
            [
                new("f1", start, taskA),
                new("f2", taskA, taskB),
                new("f3", taskB, throwComp),
                new("f4", throwComp, end)
            ]
        };

        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        await WaitForCondition(instanceId, s => s.IsCompleted, timeoutMs: 10000);

        // Act
        var entries = await Service.GetCompensationLog(instanceId);

        // Assert
        Assert.AreEqual(2, entries.Count, "Both compensable activities should be in the log");

        // Ordering by CompletedAtSequence ascending — task_a completed first.
        Assert.IsTrue(entries[0].CompletedAtSequence < entries[1].CompletedAtSequence,
            "Entries should be ordered ascending by CompletedAtSequence");

        var entryA = entries.Single(e => e.CompensableActivityId == "task_a");
        var entryB = entries.Single(e => e.CompensableActivityId == "task_b");

        Assert.AreEqual("handler_a", entryA.HandlerActivityId, "task_a's handler must resolve to handler_a");
        Assert.AreEqual("handler_b", entryB.HandlerActivityId, "task_b's handler must resolve to handler_b");
        Assert.IsTrue(entryA.IsCompensated, "task_a's snapshot must be marked compensated");
        Assert.IsTrue(entryB.IsCompensated, "task_b's snapshot must be marked compensated");

        // Root-scope entries: ScopeId is null
        Assert.IsNull(entryA.ScopeId, "Root-scope entry should have null ScopeId");
        Assert.IsNull(entryB.ScopeId, "Root-scope entry should have null ScopeId");
    }

    [TestMethod]
    public async Task NestedSubProcessScope_HandlerResolvedViaSubProcessActivities()
    {
        // Arrange: compensable activity inside a SubProcess.
        // root: start → sub → end
        // sub : sub_start → task_x → throw_sub → sub_end
        //       cb_x on task_x → handler_x  (both inside the sub)
        // After completion the entry's ScopeId is the SubProcess's host activity instance id (non-null);
        // the handler must be resolved by descending into the SubProcess's Activities — which is
        // exactly what IWorkflowDefinition.FindScopeForActivity does when the activity is nested.
        var subStart = new StartEvent("sub_start");
        var taskX = AutoTask("task_x");
        var cbX = new CompensationBoundaryEvent("cb_x", "task_x", "handler_x");
        var handlerX = AutoTask("handler_x");
        var throwSub = new CompensationIntermediateThrowEvent("throw_sub", null);
        var subEnd = new EndEvent("sub_end");

        var sub = new SubProcess("sub")
        {
            Activities = [subStart, taskX, cbX, handlerX, throwSub, subEnd],
            SequenceFlows =
            [
                new("sf1", subStart, taskX),
                new("sf2", taskX, throwSub),
                new("sf3", throwSub, subEnd)
            ]
        };

        var rootStart = new StartEvent("start");
        var rootEnd = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "compensation-nested-scope",
            Activities = [rootStart, sub, rootEnd],
            SequenceFlows =
            [
                new("rf1", rootStart, sub),
                new("rf2", sub, rootEnd)
            ]
        };

        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        await WaitForCondition(instanceId, s => s.IsCompleted, timeoutMs: 10000);

        // Act
        var entries = await Service.GetCompensationLog(instanceId);

        // Assert
        Assert.AreEqual(1, entries.Count, "Exactly one compensable activity ran in the nested scope");
        var entry = entries[0];
        Assert.AreEqual("task_x", entry.CompensableActivityId);
        Assert.AreEqual("handler_x", entry.HandlerActivityId,
            "Handler must be resolved by descending into the SubProcess's activities");
        Assert.IsTrue(entry.IsCompensated, "Nested-scope snapshot must be marked compensated after walk");
        Assert.IsNotNull(entry.ScopeId, "Nested-scope entry must carry the SubProcess host's activity instance id");
    }
}
