using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

[TestClass]
public class EventSubProcessTimerTests : WorkflowTestBase
{
    [TestMethod]
    public async Task TimerEventSubProcess_FiresExternally_CancelsSiblingsAndRunsHandler()
    {
        // Arrange: start -> userTask (blocks indefinitely) -> end
        // plus a timer-triggered interrupting event sub-process:
        //   timerStart(PT30S) -> handlerTask -> handlerEnd
        var start = new StartEvent("start");
        var userTask = new TaskActivity("userTask");
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
            IsInterrupting = true
        };

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-event-subprocess-integration",
            Activities = [start, userTask, end, evtSub],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, userTask),
                new SequenceFlow("f2", userTask, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();

        // Act: simulate the timer firing externally. Host id for a root-scope
        // event-sub timer is the workflow instance id itself (see slice C plan).
        await workflowInstance.HandleTimerFired("evtSub1_timerStart", instanceId);

        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);

        // Assert
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should have reached a terminal state");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);

        // userTask was cancelled by the interrupting event sub-process
        var userEntry = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "userTask");
        Assert.IsNotNull(userEntry, "userTask should appear in terminal activity list");
        Assert.IsTrue(userEntry.IsCancelled,
            "userTask must be cancelled by the interrupting timer event sub-process");

        // handlerTask ran inside the event sub-process
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handlerTask"
                                                              && a.ErrorState == null
                                                              && !a.IsCancelled),
            "handlerTask should have completed successfully");

        // EventSubProcess host completed
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "evtSub1"),
            "EventSubProcess host should be completed");

        // The EndEvent inside the event sub-process must also be reached
        // (closes the visibility gap flagged in #283 review round 2).
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "evtSub1_end"
                                                              && a.ErrorState == null
                                                              && !a.IsCancelled),
            "EventSubProcess inner EndEvent 'evtSub1_end' should be completed");

        // Normal 'end' was NOT reached
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Normal 'end' event should not be reached when the timer handler interrupts flow");
    }

    [TestMethod]
    public async Task TimerEventSubProcess_HandlerFails_StillReachesTerminalState()
    {
        // Regression for issue #283: when a script inside a timer-triggered
        // event sub-process throws (e.g. invalid expression), the EventSubProcess
        // scope must still close so the workflow can terminate. Previously the
        // scope auto-completion guard treated any errored child as "block forever",
        // leaving timerEventSub stuck in IsExecuting=true.
        var start = new StartEvent("start");
        var userTask = new Fleans.Domain.Activities.UserTask(
            "userTask", null, [], [], null);
        var end = new EndEvent("normalEnd");

        var timerStart = new TimerStartEvent("timerStart",
            new TimerDefinition(TimerType.Duration, "PT30S"));
        // The SimpleScriptExecutor in WorkflowTestBase throws on the magic "FAIL"
        // script, mirroring DynamicExpresso failing on an invalid expression in
        // the real Aspire stack.
        var handlerTask = new ScriptTask("handlerTask", "FAIL");
        var handlerEnd = new EndEvent("handlerEnd");
        var evtSub = new EventSubProcess("timerEventSub")
        {
            Activities = [timerStart, handlerTask, handlerEnd],
            SequenceFlows =
            [
                new SequenceFlow("esf1", timerStart, handlerTask),
                new SequenceFlow("esf2", handlerTask, handlerEnd)
            ],
            IsInterrupting = true
        };

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-event-sub-handler-fails",
            Activities = [start, userTask, end, evtSub],
            SequenceFlows =
            [
                new SequenceFlow("sf1", start, userTask),
                new SequenceFlow("sf2", userTask, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();

        await workflowInstance.HandleTimerFired("timerStart", instanceId);

        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted,
            "Workflow must terminate even when the event sub-process handler fails");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);

        // The failed handler is preserved with its error state for inspection.
        var handler = snapshot.CompletedActivities.SingleOrDefault(a => a.ActivityId == "handlerTask");
        Assert.IsNotNull(handler);
        Assert.IsNotNull(handler!.ErrorState,
            "handlerTask must retain its error state for diagnostics");

        // The EventSubProcess host is closed so the workflow can finalize.
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "timerEventSub"),
            "timerEventSub host must be marked completed once the failed handler closes the scope");
    }

    [TestMethod]
    [Ignore("Tracks a newly discovered nested-scope defect — see #284. "
        + "When a timer EventSubProcess is declared inside a SubProcess, firing "
        + "the timer throws 'Expected at most one child scope for subprocess host' "
        + "from WorkflowExecution.CompleteFinishedSubProcessScopes. Re-enable when "
        + "#284 is fixed.")]
    public async Task TimerEventSubProcess_NestedInsideSubProcess_CompletesScope()
    {
        // Regression for #283 review round 2: ensures scope-id resolution and
        // auto-completion work correctly when the timer event sub-process is
        // declared inside a SubProcess (not at the workflow root). The inner
        // SubProcess's pending user task must be cancelled by the interrupting
        // handler, the handler chain must run to its EndEvent, the ESP host
        // must close, the outer SubProcess must complete, and the workflow
        // must terminate.
        var start = new StartEvent("start");
        var end = new EndEvent("end");

        var innerStart = new StartEvent("innerStart");
        var innerUserTask = new TaskActivity("innerUserTask");
        var innerEnd = new EndEvent("innerEnd");

        var timerStart = new TimerStartEvent("nestedTimerStart",
            new TimerDefinition(TimerType.Duration, "PT30S"));
        var handlerEnd = new EndEvent("nestedHandlerEnd");
        var nestedEvtSub = new EventSubProcess("nestedEvtSub")
        {
            Activities = [timerStart, handlerEnd],
            SequenceFlows =
            [
                new SequenceFlow("nested_sf1", timerStart, handlerEnd),
            ],
            IsInterrupting = true,
        };

        var outerSub = new SubProcess("outerSub")
        {
            Activities = [innerStart, innerUserTask, innerEnd, nestedEvtSub],
            SequenceFlows =
            [
                new SequenceFlow("outer_sf1", innerStart, innerUserTask),
                new SequenceFlow("outer_sf2", innerUserTask, innerEnd),
            ],
        };

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-event-subprocess-nested",
            Activities = [start, outerSub, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, outerSub),
                new SequenceFlow("f2", outerSub, end),
            ],
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();

        // The nested timer is keyed to the outer SubProcess's activity instance id,
        // not the workflow root. Look it up from the live snapshot.
        var running = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(running);
        var outerSubEntry = running!.ActiveActivities
            .FirstOrDefault(a => a.ActivityId == "outerSub");
        Assert.IsNotNull(outerSubEntry,
            "Outer SubProcess host must be active while inner user task blocks");

        // Act: fire the nested timer with the outer SubProcess instance as host.
        await workflowInstance.HandleTimerFired(
            "nestedTimerStart", outerSubEntry!.ActivityInstanceId);

        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);

        // Assert
        Assert.IsTrue(snapshot!.IsCompleted,
            "Nested timer ESP must terminate the workflow through outer SubProcess");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);

        // Inner user task was cancelled by the interrupting nested ESP
        var inner = snapshot.CompletedActivities
            .FirstOrDefault(a => a.ActivityId == "innerUserTask");
        Assert.IsNotNull(inner, "innerUserTask must appear in terminal activity list");
        Assert.IsTrue(inner!.IsCancelled,
            "innerUserTask must be cancelled by the nested interrupting timer ESP");

        // Handler chain fully executed
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "nestedHandlerEnd"
                                                              && a.ErrorState == null
                                                              && !a.IsCancelled),
            "nestedHandlerEnd must be completed (scope-id resolution inside nested ESP)");

        // Nested ESP host closed and outer SubProcess completed
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "nestedEvtSub"),
            "Nested EventSubProcess host should be marked completed");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "outerSub"),
            "Outer SubProcess must complete once its child scopes are closed");

        // Normal outer 'end' NOT reached because the interrupting handler short-circuits it
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "innerEnd"),
            "innerEnd should not be reached when the handler interrupts the inner flow");
    }
}
