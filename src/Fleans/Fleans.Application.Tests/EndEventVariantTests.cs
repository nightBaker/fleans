using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Error, terminate and signal end events (#843) and escalation event sub-processes (#783).
/// </summary>
[TestClass]
public class EndEventVariantTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ErrorEnd_InsideASubProcess_FiresTheErrorBoundary()
    {
        // Arrange — start → sub(subStart → failEnd[E1]) → end; error boundary E1 on sub → recovered
        var subStart = new StartEvent("subStart");
        var failEnd = new ErrorEndEvent("failEnd", "E1");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, failEnd],
            SequenceFlows = [new SequenceFlow("s1", subStart, failEnd)]
        };
        var start = new StartEvent("start");
        var boundary = new BoundaryErrorEvent("onE1", "sub", "E1");
        var recovered = new TaskActivity("recovered");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "error-end-boundary",
            Activities = [start, sub, boundary, recovered, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, end),
                new SequenceFlow("f3", boundary, recovered)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow);

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("recovered") || s.IsCompleted);
        CollectionAssert.Contains(snapshot.ActiveActivityIds, "recovered");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
        var failed = snapshot.CompletedActivities.Single(a => a.ActivityId == "failEnd");
        Assert.AreEqual("E1", failed.ErrorState?.Code);
    }

    [TestMethod]
    public async Task ErrorEnd_Uncaught_FailsTheInstance()
    {
        // Arrange — start → failEnd[E1]
        var start = new StartEvent("start");
        var failEnd = new ErrorEndEvent("failEnd", "E1");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "error-end-uncaught",
            Activities = [start, failEnd],
            SequenceFlows = [new SequenceFlow("f1", start, failEnd)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow);

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsTrue(snapshot.IsFailed, "an uncaught error does not complete the instance");
    }

    [TestMethod]
    public async Task TerminateEnd_WithAnActiveParallelBranch_EndsTheInstance()
    {
        // Arrange — start → fork → (waiting) | (kill → terminate)
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var waiting = new TaskActivity("waiting");
        var kill = new TaskActivity("kill");
        var terminate = new TerminateEndEvent("terminate");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "terminate-end",
            Activities = [start, fork, waiting, kill, terminate, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                new SequenceFlow("f2", fork, waiting),
                new SequenceFlow("f3", fork, kill),
                new SequenceFlow("f4", kill, terminate),
                new SequenceFlow("f5", waiting, end)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("waiting") && s.ActiveActivityIds.Contains("kill"));

        // Act
        await instance.CompleteActivity("kill", new ExpandoObject());

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "waiting" && a.IsCancelled));
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task TerminateEnd_InsideASubProcess_EndsOnlyTheSubProcess()
    {
        // Arrange — start → sub(fork → waiting | terminate) → after
        var subStart = new StartEvent("subStart");
        var fork = new ParallelGateway("fork", IsFork: true);
        var waiting = new TaskActivity("waiting");
        var terminate = new TerminateEndEvent("terminate");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, fork, waiting, terminate],
            SequenceFlows =
            [
                new SequenceFlow("s1", subStart, fork),
                new SequenceFlow("s2", fork, waiting),
                new SequenceFlow("s3", fork, terminate)
            ]
        };
        var start = new StartEvent("start");
        var after = new TaskActivity("after");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "terminate-end-sub",
            Activities = [start, sub, after, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, after),
                new SequenceFlow("f3", after, end)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow);

        // Assert — the sub-process ends and the parent carries on
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("after") || s.IsCompleted);
        CollectionAssert.Contains(snapshot.ActiveActivityIds, "after");
        CollectionAssert.DoesNotContain(snapshot.ActiveActivityIds, "waiting");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "sub" && !a.IsCancelled));
    }

    [TestMethod]
    public async Task SignalEnd_BroadcastsItsSignal()
    {
        // Arrange — a waiting instance catches the signal the other one ends with
        var signalName = $"end-signal-{Guid.NewGuid():N}";
        var catcherWorkflow = new WorkflowDefinition
        {
            WorkflowId = "signal-end-catcher",
            Activities = [new StartEvent("start"), new SignalIntermediateCatchEvent("wait", "sig"), new EndEvent("end")],
            SequenceFlows = [],
            Signals = [new SignalDefinition("sig", signalName)]
        };
        catcherWorkflow = catcherWorkflow with
        {
            SequenceFlows =
            [
                new SequenceFlow("f1", catcherWorkflow.Activities[0], catcherWorkflow.Activities[1]),
                new SequenceFlow("f2", catcherWorkflow.Activities[1], catcherWorkflow.Activities[2])
            ]
        };
        var catcher = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await catcher.StartWorkflow(catcherWorkflow);
        await WaitForCondition(catcher.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("wait"));

        var start = new StartEvent("start");
        var signalEnd = new SignalEndEvent("signalEnd", "sig");
        var thrower = new WorkflowDefinition
        {
            WorkflowId = "signal-end-thrower",
            Activities = [start, signalEnd],
            SequenceFlows = [new SequenceFlow("f1", start, signalEnd)],
            Signals = [new SignalDefinition("sig", signalName)]
        };

        // Act — the catch subscribes asynchronously: throw again until a broadcast finds it
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var caught = false;
        while (!caught && DateTime.UtcNow < deadline)
        {
            var throwerInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await throwerInstance.StartWorkflow(thrower);
            var throwerDone = await WaitForCondition(throwerInstance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
            Assert.IsTrue(throwerDone.IsCompleted, "a signal end event completes its instance");
            await Task.Delay(200);
            caught = (await QueryService.GetStateSnapshot(catcher.GetPrimaryKey()))!.IsCompleted;
        }

        // Assert
        Assert.IsTrue(caught, "the signal end event's broadcast reached the catch");
    }

    [TestMethod]
    public async Task EscalationThrownInSubProcess_CaughtByNonInterruptingEventSubProcess_BothPathsComplete()
    {
        // Arrange — start → work(throw ESC → afterThrow) → end; root non-interrupting ESP on ESC → handler
        var workStart = new StartEvent("workStart");
        var throwEsc = new EscalationIntermediateThrowEvent("throwEscalation", "ESC");
        var afterThrow = new TaskActivity("afterThrow");
        var workEnd = new EndEvent("workEnd");
        var work = new SubProcess("work")
        {
            Activities = [workStart, throwEsc, afterThrow, workEnd],
            SequenceFlows =
            [
                new SequenceFlow("w1", workStart, throwEsc),
                new SequenceFlow("w2", throwEsc, afterThrow),
                new SequenceFlow("w3", afterThrow, workEnd)
            ]
        };
        var escStart = new EscalationStartEvent("escStart", "ESC");
        var handler = new TaskActivity("escalationHandler");
        var espEnd = new EndEvent("espEnd");
        var esp = new EventSubProcess("onEscalation")
        {
            Activities = [escStart, handler, espEnd],
            SequenceFlows = [new SequenceFlow("e1", escStart, handler), new SequenceFlow("e2", handler, espEnd)],
            IsInterrupting = false
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "escalation-esp",
            Activities = [start, work, end, esp],
            SequenceFlows = [new SequenceFlow("f1", start, work), new SequenceFlow("f2", work, end)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow);
        await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivityIds.Contains("escalationHandler") && s.ActiveActivityIds.Contains("afterThrow"));
        await instance.CompleteActivity("escalationHandler", new ExpandoObject());
        await instance.CompleteActivity("afterThrow", new ExpandoObject());

        // Assert
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsTrue(done.IsCompleted);
        CollectionAssert.IsSubsetOf(new[] { "escalationHandler", "afterThrow", "end" }, done.CompletedActivityIds.ToList());
    }

    [TestMethod]
    public async Task EscalationThrownInSubProcess_CaughtByInterruptingEventSubProcess_CancelsTheScope()
    {
        // Arrange — as above, interrupting
        var workStart = new StartEvent("workStart");
        var throwEsc = new EscalationIntermediateThrowEvent("throwEscalation", "ESC");
        var afterThrow = new TaskActivity("afterThrow");
        var workEnd = new EndEvent("workEnd");
        var work = new SubProcess("work")
        {
            Activities = [workStart, throwEsc, afterThrow, workEnd],
            SequenceFlows =
            [
                new SequenceFlow("w1", workStart, throwEsc),
                new SequenceFlow("w2", throwEsc, afterThrow),
                new SequenceFlow("w3", afterThrow, workEnd)
            ]
        };
        var esp = new EventSubProcess("onEscalation")
        {
            Activities = [new EscalationStartEvent("escStart", null), new TaskActivity("escalationHandler"), new EndEvent("espEnd")],
            IsInterrupting = true
        };
        esp = esp with
        {
            SequenceFlows =
            [
                new SequenceFlow("e1", esp.Activities[0], esp.Activities[1]),
                new SequenceFlow("e2", esp.Activities[1], esp.Activities[2])
            ]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "escalation-esp-interrupting",
            Activities = [start, work, end, esp],
            SequenceFlows = [new SequenceFlow("f1", start, work), new SequenceFlow("f2", work, end)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow);
        var handling = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("escalationHandler"));
        Assert.IsFalse(handling.ActiveActivityIds.Contains("afterThrow"), "the interrupting handler cancels the scope");
        await instance.CompleteActivity("escalationHandler", new ExpandoObject());

        // Assert
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsTrue(done.IsCompleted);
        CollectionAssert.DoesNotContain(done.CompletedActivityIds.ToList(), "end");
    }
}
