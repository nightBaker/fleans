using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

[TestClass]
public class EventSubProcessNonInterruptingTests : WorkflowTestBase
{
    [TestMethod]
    public async Task NonInterruptingTimerEventSubProcess_FiresAlongsideParent_ParentStillCompletes()
    {
        // Arrange: start -> task1 -> end
        // plus a NON-INTERRUPTING timer event sub-process:
        //   timerStart(PT30S) -> handlerTask -> handlerEnd
        //
        // The timer fires externally BEFORE task1 completes. The handler runs in
        // parallel without cancelling task1. Then task1 completes, the handler
        // completes, and the workflow terminates normally via the root end event.
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");

        var timerStart = new TimerStartEvent("evtSub1_timerStart",
            new TimerDefinition(TimerType.Duration, "PT30S"));
        var handlerTask = new ScriptTask("handlerTask", "ok");
        var handlerEnd = new EndEvent("evtSub1_end");
        var evtSub = new EventSubProcess("evtSub1")
        {
            Activities = [timerStart, handlerTask, handlerEnd],
            SequenceFlows =
            [
                new SequenceFlow("evtSub1_sf1", timerStart, handlerTask),
                new SequenceFlow("evtSub1_sf2", handlerTask, handlerEnd)
            ],
            IsInterrupting = false
        };

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ni-timer-event-subprocess-integration",
            Activities = [start, task, end, evtSub],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();

        // Act — fire the timer externally while task1 is still active.
        await workflowInstance.HandleTimerFired("evtSub1_timerStart", instanceId);

        // task1 should still be active (non-interrupting)
        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "task1 must still be active after a non-interrupting timer fires");

        // Complete task1 to drive the workflow to normal end.
        await workflowInstance.CompleteActivity("task1", new System.Dynamic.ExpandoObject());

        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should have reached a terminal state");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);

        // Both paths completed
        var task1Entry = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(task1Entry);
        Assert.IsFalse(task1Entry.IsCancelled, "task1 must NOT be cancelled");
        Assert.IsNull(task1Entry.ErrorState);

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handlerTask"
                                                              && !a.IsCancelled),
            "handlerTask should have completed in parallel");

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "evtSub1"),
            "EventSubProcess host should be completed");

        // Normal 'end' WAS reached (non-interrupting doesn't block the normal flow)
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Normal 'end' event should be reached in the non-interrupting variant");
    }

    [TestMethod]
    public async Task NonInterruptingSignalEventSubProcess_FiresOnce_ParentContinues()
    {
        // Arrange: start -> task1 (blocks) -> end
        // plus a NON-INTERRUPTING signal event sub-process. A single broadcast
        // spawns the handler alongside the parent; task1 remains active, completes
        // after signal delivery, and the workflow ends normally.
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");

        var signalDef = new SignalDefinition("pingSigDef", "ping");
        var signalStart = new SignalStartEvent("evtSub1_signalStart", "pingSigDef");
        var handlerTask = new ScriptTask("handlerTask", "ok");
        var handlerEnd = new EndEvent("evtSub1_end");
        var evtSub = new EventSubProcess("evtSub1")
        {
            Activities = [signalStart, handlerTask, handlerEnd],
            SequenceFlows =
            [
                new SequenceFlow("evtSub1_sf1", signalStart, handlerTask),
                new SequenceFlow("evtSub1_sf2", handlerTask, handlerEnd)
            ],
            IsInterrupting = false
        };

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ni-signal-event-subprocess-integration",
            Activities = [start, task, end, evtSub],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();
        var instanceId = workflowInstance.GetPrimaryKey();

        // Deliver directly to bypass the correlation grain (same path the
        // broadcast side would hit, but avoids the signal grain timeout if the
        // test cluster is slow to route the broadcast).
        await workflowInstance.HandleSignalDelivery("evtSub1_signalStart", instanceId);

        // task1 should still be active (non-interrupting didn't cancel it)
        var mid = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(mid!.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "task1 must remain active after a non-interrupting signal fires");
        await workflowInstance.CompleteActivity("task1", new System.Dynamic.ExpandoObject());

        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        var task1Entry = snapshot.CompletedActivities.First(a => a.ActivityId == "task1");
        Assert.IsFalse(task1Entry.IsCancelled);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handlerTask" && !a.IsCancelled),
            "handlerTask should have completed");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "evtSub1"),
            "EventSubProcess host should be completed");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Normal 'end' event should be reached");
    }

    [TestMethod]
    public async Task NonInterruptingTimerEventSubProcess_ScopeIsolation_HandlerVariablesDoNotLeakToParent()
    {
        // Arrange: start -> parentTask (waits) -> end
        // Non-interrupting timer event sub-process:
        //   timerStart(PT5S) -> handlerTask (waits) -> handlerEnd
        //
        // The handler runs with a CLONE of the parent scope. Variables written via
        // CompleteActivity inside the handler scope must remain in the isolated handler
        // scope and must NOT appear in the parent scope's own variable entry.
        // We verify this by inspecting VariableStates in the final snapshot.
        var start = new StartEvent("start");
        var parentTask = new TaskActivity("parentTask");
        var end = new EndEvent("end");

        var timerStart = new TimerStartEvent("evtSubScope_timerStart",
            new TimerDefinition(TimerType.Duration, "PT5S"));
        var handlerTask = new TaskActivity("handlerTask");
        var handlerEnd = new EndEvent("evtSubScope_end");
        var evtSub = new EventSubProcess("evtSubScope")
        {
            Activities = [timerStart, handlerTask, handlerEnd],
            SequenceFlows =
            [
                new SequenceFlow("scopeSf1", timerStart, handlerTask),
                new SequenceFlow("scopeSf2", handlerTask, handlerEnd)
            ],
            IsInterrupting = false
        };

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ni-timer-scope-isolation",
            Activities = [start, parentTask, end, evtSub],
            SequenceFlows =
            [
                new SequenceFlow("sf1", start, parentTask),
                new SequenceFlow("sf2", parentTask, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();
        var instanceId = workflowInstance.GetPrimaryKey();

        // Fire the timer externally (simulates the timer firing inside the ESP scope).
        await workflowInstance.HandleTimerFired("evtSubScope_timerStart", instanceId);
        await Task.Delay(300); // give the handler a moment to activate

        // handlerTask should now be active in the isolated scope.
        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.ActiveActivities.Any(a => a.ActivityId == "handlerTask"),
            "handlerTask must be active in the handler scope");
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "parentTask"),
            "parentTask must still be active (non-interrupting did not cancel parent)");

        // Complete handlerTask with a variable that should stay isolated in the handler scope.
        var handlerVars = new System.Dynamic.ExpandoObject() as IDictionary<string, object?>;
        handlerVars["handlerVar"] = "fromHandler";
        await workflowInstance.CompleteActivity("handlerTask", (System.Dynamic.ExpandoObject)handlerVars);
        await Task.Delay(300);

        // Complete parentTask with a different variable.
        var parentVars = new System.Dynamic.ExpandoObject() as IDictionary<string, object?>;
        parentVars["parentVar"] = "fromParent";
        await workflowInstance.CompleteActivity("parentTask", (System.Dynamic.ExpandoObject)parentVars);

        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        // Both handler and parent paths completed.
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handlerTask" && !a.IsCancelled),
            "handlerTask must be completed (not cancelled)");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "parentTask" && !a.IsCancelled),
            "parentTask must be completed (not cancelled)");

        // Scope isolation: 'handlerVar' must NOT appear in any remaining variable
        // scope — the EventSubProcess's isolated handler scope is orphaned on
        // completion (same semantics as slice B), and the handler must not have
        // leaked its variables into the parent scope.
        var scopesWithHandlerVar = snapshot.VariableStates
            .Count(vs => vs.Variables.ContainsKey("handlerVar"));
        Assert.AreEqual(0, scopesWithHandlerVar,
            "'handlerVar' must not leak into any surviving variable scope");

        var parentScopeEntry = snapshot.VariableStates
            .FirstOrDefault(vs => vs.Variables.ContainsKey("parentVar"));
        Assert.IsNotNull(parentScopeEntry, "Parent scope must record 'parentVar'");
        Assert.IsFalse(parentScopeEntry.Variables.ContainsKey("handlerVar"),
            "'handlerVar' must NOT appear in the scope that owns 'parentVar' — scope isolation is broken");
    }

}
