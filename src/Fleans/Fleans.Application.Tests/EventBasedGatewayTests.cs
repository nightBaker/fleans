using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EventBasedGatewayTests : WorkflowTestBase
{
    [TestMethod]
    public async Task EventBasedGateway_MessageWins_ShouldCancelTimer()
    {
        // Arrange — Start -> Task -> EBG -> [TimerCatch(1h), MessageCatch] -> End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var ebg = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch",
            new TimerDefinition(TimerType.Duration, "PT1H"));
        var msgDef = new MessageDefinition("msg1", "paymentReceived", "orderId");
        var msgCatch = new MessageIntermediateCatchEvent("msgCatch", "msg1");
        var endTimer = new EndEvent("endTimer");
        var endMsg = new EndEvent("endMsg");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ebg-msg-wins",
            Activities = [start, task, ebg, timerCatch, msgCatch, endTimer, endMsg],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, ebg),
                new SequenceFlow("f3", ebg, timerCatch),
                new SequenceFlow("f4", ebg, msgCatch),
                new SequenceFlow("f5", timerCatch, endTimer),
                new SequenceFlow("f6", msgCatch, endMsg)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task with orderId variable (needed for message correlation)
        dynamic vars = new ExpandoObject();
        vars.orderId = "order-ebg-1";
        await workflowInstance.CompleteActivity("task1", vars);

        // Assert — workflow suspended at both catch events
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should be suspended at EBG catch events");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "timerCatch"),
            "Timer catch should be active");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "msgCatch"),
            "Message catch should be active");

        // Act — deliver message (message wins the race)
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("paymentReceived");
        dynamic msgVars = new ExpandoObject();
        msgVars.paymentStatus = "confirmed";
        var delivered = await correlationGrain.DeliverMessage("order-ebg-1", (ExpandoObject)msgVars);

        // Assert — workflow completed via message path
        Assert.IsTrue(delivered, "Message should be delivered");
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");

        // Timer catch should be cancelled
        var timerActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "timerCatch");
        Assert.IsNotNull(timerActivity, "Timer catch should be in completed list");
        Assert.IsTrue(timerActivity.IsCancelled, "Timer catch should be cancelled");

        // Message catch should be completed (not cancelled)
        var msgActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "msgCatch");
        Assert.IsNotNull(msgActivity, "Message catch should be in completed list");
        Assert.IsFalse(msgActivity.IsCancelled, "Message catch should NOT be cancelled");

        // endMsg should be reached (message path), endTimer should NOT
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endMsg"),
            "Message end event should be reached");
        Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endTimer"),
            "Timer end event should NOT be reached");
    }

    [TestMethod]
    public async Task EventBasedGateway_SignalWins_ShouldCancelTimer()
    {
        // Arrange — Start -> EBG -> [TimerCatch(1h), SignalCatch] -> End
        var start = new StartEvent("start");
        var ebg = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch",
            new TimerDefinition(TimerType.Duration, "PT1H"));
        var signalDef = new SignalDefinition("sig1", "approvalSignal");
        var signalCatch = new SignalIntermediateCatchEvent("signalCatch", "sig1");
        var endTimer = new EndEvent("endTimer");
        var endSignal = new EndEvent("endSignal");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ebg-signal-wins",
            Activities = [start, ebg, timerCatch, signalCatch, endTimer, endSignal],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, ebg),
                new SequenceFlow("f2", ebg, timerCatch),
                new SequenceFlow("f3", ebg, signalCatch),
                new SequenceFlow("f4", timerCatch, endTimer),
                new SequenceFlow("f5", signalCatch, endSignal)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Assert — workflow suspended at catch events
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted);
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "timerCatch"));
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "signalCatch"));

        // Act — broadcast signal (signal wins the race)
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("approvalSignal");
        await signalGrain.BroadcastSignal();

        // Assert — workflow completed via signal path, timer cancelled
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");

        var timerActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "timerCatch");
        Assert.IsNotNull(timerActivity);
        Assert.IsTrue(timerActivity.IsCancelled, "Timer catch should be cancelled");

        var signalActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "signalCatch");
        Assert.IsNotNull(signalActivity);
        Assert.IsFalse(signalActivity.IsCancelled, "Signal catch should NOT be cancelled");

        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endSignal"),
            "Signal end event should be reached");
        Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endTimer"),
            "Timer end event should NOT be reached");
    }

    [TestMethod]
    public async Task EventBasedGateway_ThreeWay_MessageWins_ShouldCancelTimerAndSignal()
    {
        // Arrange — Start -> Task -> EBG -> [Timer(1h), Message, Signal] -> End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var ebg = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch",
            new TimerDefinition(TimerType.Duration, "PT1H"));
        var msgDef = new MessageDefinition("msg1", "paymentReceived", "orderId");
        var msgCatch = new MessageIntermediateCatchEvent("msgCatch", "msg1");
        var signalDef = new SignalDefinition("sig1", "approvalSignal");
        var signalCatch = new SignalIntermediateCatchEvent("signalCatch", "sig1");
        var endTimer = new EndEvent("endTimer");
        var endMsg = new EndEvent("endMsg");
        var endSignal = new EndEvent("endSignal");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ebg-three-way",
            Activities = [start, task, ebg, timerCatch, msgCatch, signalCatch, endTimer, endMsg, endSignal],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, ebg),
                new SequenceFlow("f3", ebg, timerCatch),
                new SequenceFlow("f4", ebg, msgCatch),
                new SequenceFlow("f5", ebg, signalCatch),
                new SequenceFlow("f6", timerCatch, endTimer),
                new SequenceFlow("f7", msgCatch, endMsg),
                new SequenceFlow("f8", signalCatch, endSignal)
            ],
            Messages = [msgDef],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic vars = new ExpandoObject();
        vars.orderId = "order-3way";
        await workflowInstance.CompleteActivity("task1", vars);

        // Act — deliver message (message wins)
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("paymentReceived");
        await correlationGrain.DeliverMessage("order-3way", new ExpandoObject());

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");

        // Timer and signal should be cancelled
        var timerActivity = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "timerCatch");
        Assert.IsTrue(timerActivity.IsCancelled, "Timer catch should be cancelled");

        var signalActivity = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "signalCatch");
        Assert.IsTrue(signalActivity.IsCancelled, "Signal catch should be cancelled");

        // Message should NOT be cancelled
        var msgActivity = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "msgCatch");
        Assert.IsFalse(msgActivity.IsCancelled, "Message catch should NOT be cancelled");

        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endMsg"),
            "Message end event should be reached");
        Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endTimer"),
            "Timer end event should NOT be reached");
        Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endSignal"),
            "Signal end event should NOT be reached");
    }
}
