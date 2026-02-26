using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class BoundaryOnCatchEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task TimerBoundaryOnMessageCatch_TimerFires_ShouldFollowBoundaryPath()
    {
        // Arrange — Start → MessageCatch(+BoundaryTimer) → NormalEnd, BoundaryTimer → TimeoutEnd
        var start = new StartEvent("start");
        var msgDef = new MessageDefinition("msg1", "neverArrives", "corrKey");
        var msgCatch = new MessageIntermediateCatchEvent("msgCatch", "msg1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "msgCatch", timerDef);
        var normalEnd = new EndEvent("normalEnd");
        var timeoutEnd = new EndEvent("timeoutEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-boundary-on-msg-catch",
            Activities = [start, msgCatch, boundaryTimer, normalEnd, timeoutEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, msgCatch),
                new SequenceFlow("f2", msgCatch, normalEnd),
                new SequenceFlow("f3", boundaryTimer, timeoutEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.corrKey = "never-match";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        // Verify message catch is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "msgCatch"),
            "Message catch should be active");

        // Act — simulate boundary timer firing
        var hostInstanceId = preSnapshot.ActiveActivities.First(a => a.ActivityId == "msgCatch").ActivityInstanceId;
        await workflowInstance.HandleTimerFired("bt1", hostInstanceId);

        // Assert — boundary path taken, message catch interrupted
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed via timeout path");
        var interruptedCatch = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "msgCatch");
        Assert.IsNotNull(interruptedCatch, "Message catch should be completed (interrupted)");
        Assert.IsTrue(interruptedCatch.IsCancelled, "Interrupted catch should be cancelled");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "timeoutEnd"),
            "Should complete via timeout end");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "normalEnd"),
            "Should NOT complete via normal end");
    }

    [TestMethod]
    public async Task MessageBoundaryOnTimerCatch_MessageArrives_ShouldFollowBoundaryPath()
    {
        // Arrange — Start → TimerCatch(+BoundaryMessage) → NormalEnd, BoundaryMessage → CancelEnd
        var start = new StartEvent("start");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch", new TimerDefinition(TimerType.Duration, "PT60M"));
        var msgDef = new MessageDefinition("msg1", "cancelRequest", "requestId");
        var boundaryMsg = new MessageBoundaryEvent("bmsg1", "timerCatch", "msg1");
        var normalEnd = new EndEvent("normalEnd");
        var cancelEnd = new EndEvent("cancelEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "msg-boundary-on-timer-catch",
            Activities = [start, timerCatch, boundaryMsg, normalEnd, cancelEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timerCatch),
                new SequenceFlow("f2", timerCatch, normalEnd),
                new SequenceFlow("f3", boundaryMsg, cancelEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.requestId = "req-789";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        // Verify timer catch is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "timerCatch"),
            "Timer catch should be active");

        // Act — deliver boundary message
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("cancelRequest");
        var delivered = await correlationGrain.DeliverMessage("req-789", new ExpandoObject());

        // Assert — boundary path taken, timer catch interrupted
        Assert.IsTrue(delivered, "Message should be delivered");
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed via cancel path");
        var interruptedCatch = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "timerCatch");
        Assert.IsNotNull(interruptedCatch, "Timer catch should be completed (interrupted)");
        Assert.IsTrue(interruptedCatch.IsCancelled, "Interrupted catch should be cancelled");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "cancelEnd"),
            "Should complete via cancel end");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "normalEnd"),
            "Should NOT complete via normal end");
    }

    [TestMethod]
    public async Task SignalBoundaryOnTimerCatch_SignalArrives_ShouldFollowBoundaryPath()
    {
        // Arrange — Start → TimerCatch(+BoundarySignal) → NormalEnd, BoundarySignal → EmergencyEnd
        var start = new StartEvent("start");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch", new TimerDefinition(TimerType.Duration, "PT60M"));
        var signalDef = new SignalDefinition("sig1", "emergencyStop");
        var boundarySignal = new SignalBoundaryEvent("bsig1", "timerCatch", "sig1");
        var normalEnd = new EndEvent("normalEnd");
        var emergencyEnd = new EndEvent("emergencyEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-boundary-on-timer-catch",
            Activities = [start, timerCatch, boundarySignal, normalEnd, emergencyEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timerCatch),
                new SequenceFlow("f2", timerCatch, normalEnd),
                new SequenceFlow("f3", boundarySignal, emergencyEnd)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Verify timer catch is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "timerCatch"),
            "Timer catch should be active");

        // Act — broadcast boundary signal
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("emergencyStop");
        var deliveredCount = await signalGrain.BroadcastSignal();

        // Assert — boundary path taken, timer catch interrupted
        Assert.AreEqual(1, deliveredCount, "Signal should be delivered to 1 subscriber");
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed via emergency path");
        var interruptedCatch = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "timerCatch");
        Assert.IsNotNull(interruptedCatch, "Timer catch should be completed (interrupted)");
        Assert.IsTrue(interruptedCatch.IsCancelled, "Interrupted catch should be cancelled");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "emergencyEnd"),
            "Should complete via emergency end");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "normalEnd"),
            "Should NOT complete via normal end");
    }

    [TestMethod]
    public async Task TimerBoundaryOnMessageCatch_MessageArrivesFirst_ShouldFollowNormalPath()
    {
        // Arrange — Start → MessageCatch(+BoundaryTimer) → NormalEnd, BoundaryTimer → TimeoutEnd
        var start = new StartEvent("start");
        var msgDef = new MessageDefinition("msg1", "approval", "corrKey");
        var msgCatch = new MessageIntermediateCatchEvent("msgCatch", "msg1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "msgCatch", timerDef);
        var normalEnd = new EndEvent("normalEnd");
        var timeoutEnd = new EndEvent("timeoutEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-boundary-on-msg-catch-normal",
            Activities = [start, msgCatch, boundaryTimer, normalEnd, timeoutEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, msgCatch),
                new SequenceFlow("f2", msgCatch, normalEnd),
                new SequenceFlow("f3", boundaryTimer, timeoutEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.corrKey = "req-100";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        // Act — deliver the message (catch event's own trigger fires first)
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("approval");
        var delivered = await correlationGrain.DeliverMessage("req-100", new ExpandoObject());

        // Assert — normal flow, boundary not taken
        Assert.IsTrue(delivered, "Message should be delivered");
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed via normal path");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "normalEnd"),
            "Should complete via normal end");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "timeoutEnd"),
            "Should NOT complete via timeout end");
    }

    [TestMethod]
    public async Task MessageBoundaryOnTimerCatch_TimerFiresFirst_ShouldFollowNormalPath()
    {
        // Arrange — Start → TimerCatch(+BoundaryMessage) → NormalEnd, BoundaryMessage → CancelEnd
        var start = new StartEvent("start");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch", new TimerDefinition(TimerType.Duration, "PT60M"));
        var msgDef = new MessageDefinition("msg1", "cancelRequest", "requestId");
        var boundaryMsg = new MessageBoundaryEvent("bmsg1", "timerCatch", "msg1");
        var normalEnd = new EndEvent("normalEnd");
        var cancelEnd = new EndEvent("cancelEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "msg-boundary-on-timer-catch-normal",
            Activities = [start, timerCatch, boundaryMsg, normalEnd, cancelEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timerCatch),
                new SequenceFlow("f2", timerCatch, normalEnd),
                new SequenceFlow("f3", boundaryMsg, cancelEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.requestId = "req-normal";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        // Verify timer catch is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities.First(a => a.ActivityId == "timerCatch").ActivityInstanceId;

        // Act — simulate the timer catch's own timer firing (normal completion)
        await workflowInstance.HandleTimerFired("timerCatch", hostInstanceId);

        // Assert — normal flow, boundary not taken
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed via normal path");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "normalEnd"),
            "Should complete via normal end");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "cancelEnd"),
            "Should NOT complete via cancel end");

        // Verify boundary message subscription is gone
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("cancelRequest");
        var delivered = await correlationGrain.DeliverMessage("req-normal", new ExpandoObject());
        Assert.IsFalse(delivered, "Boundary message subscription should have been cleaned up");
    }

    [TestMethod]
    public async Task SignalBoundaryOnTimerCatch_TimerFiresFirst_ShouldFollowNormalPath()
    {
        // Arrange — Start → TimerCatch(+BoundarySignal) → NormalEnd, BoundarySignal → EmergencyEnd
        var start = new StartEvent("start");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch", new TimerDefinition(TimerType.Duration, "PT60M"));
        var signalDef = new SignalDefinition("sig1", "emergencyStop");
        var boundarySignal = new SignalBoundaryEvent("bsig1", "timerCatch", "sig1");
        var normalEnd = new EndEvent("normalEnd");
        var emergencyEnd = new EndEvent("emergencyEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-boundary-on-timer-catch-normal",
            Activities = [start, timerCatch, boundarySignal, normalEnd, emergencyEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timerCatch),
                new SequenceFlow("f2", timerCatch, normalEnd),
                new SequenceFlow("f3", boundarySignal, emergencyEnd)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Verify timer catch is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities.First(a => a.ActivityId == "timerCatch").ActivityInstanceId;

        // Act — simulate the timer catch's own timer firing (normal completion)
        await workflowInstance.HandleTimerFired("timerCatch", hostInstanceId);

        // Assert — normal flow, boundary not taken
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed via normal path");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "normalEnd"),
            "Should complete via normal end");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "emergencyEnd"),
            "Should NOT complete via emergency end");

        // Verify boundary signal subscription is gone
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("emergencyStop");
        var deliveredCount = await signalGrain.BroadcastSignal();
        Assert.AreEqual(0, deliveredCount, "Boundary signal subscription should have been cleaned up");
    }
}
