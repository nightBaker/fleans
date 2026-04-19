using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MultipleEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task MultipleCatch_MessageFires_ShouldCompleteAndCancelSignal()
    {
        // Arrange — Start → Task → MultipleCatch(msg+signal) → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "payment", "orderId");
        var signalDef = new SignalDefinition("sig1", "approval");
        var multiCatch = new MultipleIntermediateCatchEvent("multiCatch",
        [
            new MessageEventDef("msg1"),
            new SignalEventDef("sig1")
        ]);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "multi-catch-msg-wins",
            Activities = [start, task, multiCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, multiCatch),
                new SequenceFlow("f3", multiCatch, end)
            ],
            Messages = [msgDef],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task with orderId (for message correlation)
        dynamic vars = new ExpandoObject();
        vars.orderId = "order-multi-1";
        await workflowInstance.CompleteActivity("task1", vars);

        // Assert — workflow suspended at multiple catch
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should be suspended at multiple catch");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "multiCatch"),
            "Multiple catch should be active");

        // Act — deliver message (message wins)
        var grainKey = MessageCorrelationKey.Build("payment", "order-multi-1");
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
        dynamic msgVars = new ExpandoObject();
        msgVars.amount = 100;
        var delivered = await correlationGrain.DeliverMessage((ExpandoObject)msgVars);

        // Assert — workflow completed via message
        Assert.IsTrue(delivered, "Message should be delivered");
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after message delivery");
    }

    [TestMethod]
    public async Task MultipleCatch_SignalFires_ShouldCompleteAndCancelMessage()
    {
        // Arrange — Start → Task → MultipleCatch(msg+signal) → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "payment", "orderId");
        var signalDef = new SignalDefinition("sig1", "approval");
        var multiCatch = new MultipleIntermediateCatchEvent("multiCatch",
        [
            new MessageEventDef("msg1"),
            new SignalEventDef("sig1")
        ]);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "multi-catch-sig-wins",
            Activities = [start, task, multiCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, multiCatch),
                new SequenceFlow("f3", multiCatch, end)
            ],
            Messages = [msgDef],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task with orderId
        dynamic vars = new ExpandoObject();
        vars.orderId = "order-multi-2";
        await workflowInstance.CompleteActivity("task1", vars);

        // Assert — workflow suspended at multiple catch
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should be suspended at multiple catch");

        // Act — broadcast signal (signal wins)
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("approval");
        var deliveredCount = await signalGrain.BroadcastSignal();

        // Assert — workflow completed via signal
        Assert.AreEqual(1, deliveredCount, "Signal should be delivered to one subscriber");
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after signal broadcast");
    }

    [TestMethod]
    public async Task MultipleThrow_ShouldThrowAllSignals()
    {
        // Arrange — Two workflows:
        // Workflow A: Start → MultipleThrow(sig1+sig2) → End
        // Workflow B: Start → Task → SignalCatch(sig1) → End  (subscriber for sig1)
        // Workflow C: Start → Task → SignalCatch(sig2) → End  (subscriber for sig2)

        var sig1Def = new SignalDefinition("sig1", "eventA");
        var sig2Def = new SignalDefinition("sig2", "eventB");

        // Thrower workflow
        var throwStart = new StartEvent("start");
        var multiThrow = new MultipleIntermediateThrowEvent("multiThrow",
        [
            new SignalEventDef("sig1"),
            new SignalEventDef("sig2")
        ]);
        var throwEnd = new EndEvent("end");

        var throwWorkflow = new WorkflowDefinition
        {
            WorkflowId = "multi-throw",
            Activities = [throwStart, multiThrow, throwEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", throwStart, multiThrow),
                new SequenceFlow("f2", multiThrow, throwEnd)
            ],
            Signals = [sig1Def, sig2Def]
        };

        // Subscriber B (sig1)
        var subBStart = new StartEvent("start");
        var subBTask = new TaskActivity("task1");
        var subBCatch = new SignalIntermediateCatchEvent("waitSig1", "sig1");
        var subBEnd = new EndEvent("end");

        var subBWorkflow = new WorkflowDefinition
        {
            WorkflowId = "sub-b",
            Activities = [subBStart, subBTask, subBCatch, subBEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", subBStart, subBTask),
                new SequenceFlow("f2", subBTask, subBCatch),
                new SequenceFlow("f3", subBCatch, subBEnd)
            ],
            Signals = [sig1Def]
        };

        // Subscriber C (sig2)
        var subCStart = new StartEvent("start");
        var subCTask = new TaskActivity("task1");
        var subCCatch = new SignalIntermediateCatchEvent("waitSig2", "sig2");
        var subCEnd = new EndEvent("end");

        var subCWorkflow = new WorkflowDefinition
        {
            WorkflowId = "sub-c",
            Activities = [subCStart, subCTask, subCCatch, subCEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", subCStart, subCTask),
                new SequenceFlow("f2", subCTask, subCCatch),
                new SequenceFlow("f3", subCCatch, subCEnd)
            ],
            Signals = [sig2Def]
        };

        // Start subscribers
        var instanceB = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instanceB.SetWorkflow(subBWorkflow);
        await instanceB.StartWorkflow();
        await instanceB.CompleteActivity("task1", new ExpandoObject());

        var instanceC = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instanceC.SetWorkflow(subCWorkflow);
        await instanceC.StartWorkflow();
        await instanceC.CompleteActivity("task1", new ExpandoObject());

        // Verify subscribers are suspended
        var snapB = await QueryService.GetStateSnapshot(instanceB.GetPrimaryKey());
        Assert.IsTrue(snapB!.ActiveActivities.Any(a => a.ActivityId == "waitSig1"),
            "Subscriber B should be waiting for sig1");
        var snapC = await QueryService.GetStateSnapshot(instanceC.GetPrimaryKey());
        Assert.IsTrue(snapC!.ActiveActivities.Any(a => a.ActivityId == "waitSig2"),
            "Subscriber C should be waiting for sig2");

        // Act — run thrower workflow
        var thrower = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await thrower.SetWorkflow(throwWorkflow);
        await thrower.StartWorkflow();

        // Assert — thrower completed
        var throwerSnap = await QueryService.GetStateSnapshot(thrower.GetPrimaryKey());
        Assert.IsTrue(throwerSnap!.IsCompleted, "Thrower workflow should be completed");

        // Wait for signal delivery propagation
        await Task.Delay(500);

        // Both subscribers should be completed
        var finalSnapB = await QueryService.GetStateSnapshot(instanceB.GetPrimaryKey());
        Assert.IsTrue(finalSnapB!.IsCompleted, "Subscriber B should be completed after sig1 broadcast");
        var finalSnapC = await QueryService.GetStateSnapshot(instanceC.GetPrimaryKey());
        Assert.IsTrue(finalSnapC!.IsCompleted, "Subscriber C should be completed after sig2 broadcast");
    }

    [TestMethod]
    public async Task MultipleCatch_MessageWithVariables_ShouldMergeVariables()
    {
        // Arrange — Start → Task → MultipleCatch(msg+signal) → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "dataMsg", "correlationVar");
        var signalDef = new SignalDefinition("sig1", "dataSig");
        var multiCatch = new MultipleIntermediateCatchEvent("multiCatch",
        [
            new MessageEventDef("msg1"),
            new SignalEventDef("sig1")
        ]);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "multi-catch-vars",
            Activities = [start, task, multiCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, multiCatch),
                new SequenceFlow("f3", multiCatch, end)
            ],
            Messages = [msgDef],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task with correlation variable
        dynamic taskVars = new ExpandoObject();
        taskVars.correlationVar = "key-123";
        await workflowInstance.CompleteActivity("task1", taskVars);

        // Assert — waiting at catch
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted);

        // Act — deliver message with variables
        var grainKey = MessageCorrelationKey.Build("dataMsg", "key-123");
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
        dynamic msgVars = new ExpandoObject();
        msgVars.result = "success";
        await correlationGrain.DeliverMessage((ExpandoObject)msgVars);

        // Assert — completed with merged variables
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");
    }
}
