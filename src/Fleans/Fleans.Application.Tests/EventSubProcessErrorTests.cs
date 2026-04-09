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
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

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
        Assert.AreEqual(500, failingEntry.ErrorState!.Code,
            "Generic Exception should map to error code 500");

        // 3. handlerTask ran successfully
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handlerTask"
                                                             && a.ErrorState == null),
            "handlerTask should have completed successfully");

        // 4. EventSubProcess host completed
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "evtSub1"),
            "EventSubProcess host should be marked completed");

        // 5. Sibling 'end' was NOT reached
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Normal 'end' event should not be reached when the error handler interrupts flow");
    }

}
