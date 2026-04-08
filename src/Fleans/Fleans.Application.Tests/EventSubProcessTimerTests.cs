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

        // Normal 'end' was NOT reached
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Normal 'end' event should not be reached when the timer handler interrupts flow");
    }

    private async Task<InstanceStateSnapshot?> PollForNoActiveActivities(
        Guid instanceId, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            if (snapshot is not null && snapshot.ActiveActivities.Count == 0)
                return snapshot;
            await Task.Delay(100);
        }
        return await QueryService.GetStateSnapshot(instanceId);
    }
}
