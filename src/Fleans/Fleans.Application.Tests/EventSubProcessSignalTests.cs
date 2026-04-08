using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

[TestClass]
public class EventSubProcessSignalTests : WorkflowTestBase
{
    [TestMethod]
    public async Task SignalEventSubProcess_Broadcast_CancelsSiblingsAndRunsHandler()
    {
        // Arrange: start -> userTask (blocks) -> end
        // plus a signal-triggered interrupting event sub-process:
        //   signalStart("cancelEverything") -> handlerTask -> handlerEnd
        var start = new StartEvent("start");
        var userTask = new TaskActivity("userTask");
        var end = new EndEvent("end");

        var signalDef = new SignalDefinition("cancelSigDef", "cancelEverything");
        var signalStart = new SignalStartEvent("evtSub1_signalStart", "cancelSigDef");
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
            IsInterrupting = true
        };

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-event-subprocess-integration",
            Activities = [start, userTask, end, evtSub],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, userTask),
                new SequenceFlow("f2", userTask, end)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        await Task.Delay(500); // let the signal subscription register

        // Act — broadcast the signal.
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("cancelEverything");
        var deliveredCount = await signalGrain.BroadcastSignal();

        Assert.IsTrue(deliveredCount >= 1,
            $"Signal should be delivered to at least one subscriber (got {deliveredCount})");

        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should have reached a terminal state");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);

        var userEntry = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "userTask");
        Assert.IsNotNull(userEntry, "userTask should appear in terminal activity list");
        Assert.IsTrue(userEntry.IsCancelled,
            "userTask must be cancelled by the interrupting signal event sub-process");

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handlerTask"
                                                              && a.ErrorState == null
                                                              && !a.IsCancelled),
            "handlerTask should have completed successfully");

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "evtSub1"),
            "EventSubProcess host should be completed");

        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Normal 'end' event should not be reached when the signal handler interrupts flow");
    }

}
