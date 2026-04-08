using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EventSubProcessMessageTests : WorkflowTestBase
{
    [TestMethod]
    public async Task MessageEventSubProcess_CorrelatedDelivery_CancelsSiblingsAndRunsHandler()
    {
        // Arrange: start -> userTask (blocks) -> end
        // plus a message-triggered interrupting event sub-process correlated by
        // `orderId` — the correlation variable must be set before StartWorkflow
        // because registration happens at scope entry and resolves the key
        // against the current variables snapshot.
        var start = new StartEvent("start");
        var userTask = new TaskActivity("userTask");
        var end = new EndEvent("end");

        var msgStart = new MessageStartEvent("evtSub1_msgStart", "cancelMsgDef");
        var handlerTask = new ScriptTask("handlerTask", "ok");
        var handlerEnd = new EndEvent("evtSub1_end");
        var evtSub = new EventSubProcess("evtSub1")
        {
            Activities = [msgStart, handlerTask, handlerEnd],
            SequenceFlows =
            [
                new SequenceFlow("evtSub1_sf1", msgStart, handlerTask),
                new SequenceFlow("evtSub1_sf2", handlerTask, handlerEnd)
            ],
            IsInterrupting = true
        };

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "message-event-subprocess-integration",
            Activities = [start, userTask, end, evtSub],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, userTask),
                new SequenceFlow("f2", userTask, end)
            ],
            Messages = [new MessageDefinition("cancelMsgDef", "cancelOrder", "= orderId")]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        dynamic initVars = new ExpandoObject();
        initVars.orderId = "ORD-123";
        await workflowInstance.SetInitialVariables((ExpandoObject)initVars);
        await workflowInstance.StartWorkflow();

        // Wait for the userTask to be active and the subscription to register.
        var instanceId = workflowInstance.GetPrimaryKey();
        await Task.Delay(500);

        // Act — deliver a correlated message via the correlation grain.
        var correlationKey = MessageCorrelationKey.Build("cancelOrder", "ORD-123");
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(correlationKey);
        var delivered = await correlationGrain.DeliverMessage(new ExpandoObject());

        Assert.IsTrue(delivered, "Correlated message should be delivered to the event sub-process subscription");

        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);

        Assert.IsTrue(snapshot.IsCompleted, "Workflow should have reached a terminal state");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);

        var userEntry = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "userTask");
        Assert.IsNotNull(userEntry, "userTask should appear in terminal activity list");
        Assert.IsTrue(userEntry.IsCancelled,
            "userTask must be cancelled by the interrupting message event sub-process");

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handlerTask"
                                                              && a.ErrorState == null
                                                              && !a.IsCancelled),
            "handlerTask should have completed successfully");

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "evtSub1"),
            "EventSubProcess host should be completed");

        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Normal 'end' event should not be reached when the message handler interrupts flow");
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
