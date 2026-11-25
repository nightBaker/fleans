using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

[TestClass]
public class EventSubProcessErrorTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ErrorEventSubProcess_CatchesScriptTaskFailure_AndCompletesWorkflow()
    {
        // Arrange: start -> failingTask (throws via "FAIL" marker) -> end
        // plus an interrupting error event sub-process catching code "500":
        //   errStart("500") -> handlerTask -> errEnd
        var start = new StartEvent("start");
        var failingTask = new ScriptTask("failingTask", "FAIL");
        var end = new EndEvent("end");

        var errStart = new ErrorStartEvent("evtSub1_errStart", "500");
        var handlerTask = new ScriptTask("handlerTask", "ok");
        var errEnd = new EndEvent("evtSub1_errEnd");
        var evtSub = new EventSubProcess("evtSub1")
        {
            Activities = [errStart, handlerTask, errEnd],
            SequenceFlows =
            [
                new SequenceFlow("evtSub1_sf1", errStart, handlerTask),
                new SequenceFlow("evtSub1_sf2", handlerTask, errEnd)
            ],
            IsInterrupting = true
        };

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "error-event-subprocess-integration",
            Activities = [start, failingTask, end, evtSub],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, failingTask),
                new SequenceFlow("f2", failingTask, end)
            ]
        };

        // Act
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.StartWorkflow(workflow);

        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);

        // 1. Terminal state
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should have reached a terminal state");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count, "No activities should remain active");

        // 2. failingTask failed with code 500 (generic Exception)
        var failingEntry = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "failingTask");
        Assert.IsNotNull(failingEntry, "failingTask should appear in the completed-activities list");
        Assert.IsNotNull(failingEntry.ErrorState, "failingTask should have an error state");
        Assert.AreEqual("500", failingEntry.ErrorState!.Code,
            "Generic Exception should map to error code 500");

        // 3. handlerTask ran successfully
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handlerTask"
                                                             && a.ErrorState == null),
            "handlerTask should have completed successfully");

        // 4. EventSubProcess host completed
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "evtSub1"),
            "EventSubProcess host should be marked completed");

        // 4a. The EndEvent inside the error event sub-process must be recorded.
        // Closes the coverage gap in #285 plan v2 — Error ESPs were never broken,
        // but we want a regression guard if the shared completion path drifts.
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "evtSub1_errEnd"
                                                              && a.ErrorState == null
                                                              && !a.IsCancelled),
            "Error EventSubProcess inner EndEvent 'evtSub1_errEnd' should be completed");

        // NOTE: the v2 plan additionally proposed an ordering invariant
        // (EndEvent index < host index in CompletedActivities). Implementation
        // revealed the projection's Entries collection has no OrderBy clause
        // (see WorkflowQueryService.GetStateSnapshot), so its list ordering is
        // not a stable invariant — the assertion was flaky across runs even on
        // the interrupting path. Presence is what catches the regression; a
        // stable-ordering surface is tracked as a follow-up.

        // 5. Sibling 'end' was NOT reached
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Normal 'end' event should not be reached when the error handler interrupts flow");
    }


    [TestMethod]
    [Ignore("Blocked by #855: the error-boundary search stops at an event sub-process, so a failure inside the ESP handler never reaches the boundary on the enclosing sub-process.")]
    public async Task FailureInsideAnErrorEspHandler_ReachesTheErrorBoundaryOfTheEnclosingSubProcess()
    {
        // Arrange — start → sub(work; error ESP "BIZ" → handler) → end; error boundary "HANDLER" on sub → recovered
        var subStart = new StartEvent("subStart");
        var work = new TaskActivity("work");
        var subEnd = new EndEvent("subEnd");
        var errStart = new ErrorStartEvent("espStart", "BIZ");
        var handler = new TaskActivity("handler");
        var espEnd = new EndEvent("espEnd");
        var esp = new EventSubProcess("onBiz")
        {
            Activities = [errStart, handler, espEnd],
            SequenceFlows =
            [
                new SequenceFlow("esp1", errStart, handler),
                new SequenceFlow("esp2", handler, espEnd)
            ],
            IsInterrupting = true
        };
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, work, subEnd, esp],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, work),
                new SequenceFlow("sf2", work, subEnd)
            ]
        };
        var start = new StartEvent("start");
        var boundary = new BoundaryErrorEvent("subError", "sub", "HANDLER");
        var recovered = new TaskActivity("recovered");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esp-handler-failure-to-boundary",
            Activities = [start, sub, boundary, recovered, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, end),
                new SequenceFlow("f3", boundary, recovered)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);
        await instance.FailActivity("work", new Domain.Errors.CustomTaskFailedActivityException("BIZ", "business error"));
        await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("handler"));

        // Act — the handler itself fails with the code the boundary catches
        await instance.FailActivity("handler", new Domain.Errors.CustomTaskFailedActivityException("HANDLER", "handler failed"));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("recovered") || s.IsFailed);
        Assert.IsFalse(snapshot.IsFailed, "the boundary on the enclosing sub-process catches the handler's error");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "sub" && a.IsCancelled));
    }
}
