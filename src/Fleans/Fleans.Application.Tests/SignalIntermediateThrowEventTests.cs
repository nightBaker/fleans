using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalIntermediateThrowEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task SignalThrow_ShouldCompleteAndContinue()
    {
        // Arrange — Start → ThrowSignal → End
        var start = new StartEvent("start");
        var signalDef = new SignalDefinition("sig1", "orderApproved");
        var throwSignal = new SignalIntermediateThrowEvent("emitApproval", "sig1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-throw-test",
            Activities = [start, throwSignal, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, throwSignal),
                new SequenceFlow("f2", throwSignal, end)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — workflow should auto-complete (throw event broadcasts signal and completes itself)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed — throw event auto-completes");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "emitApproval"),
            "Throw signal activity should be in completed activities");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "End event should be reached");
    }

    [TestMethod]
    public async Task SignalThrow_ShouldDeliverToCatchingWorkflow()
    {
        // Arrange — Workflow B catches signal, Workflow A throws it
        var signalDef = new SignalDefinition("sig1", "orderApproved");

        // Workflow B: Start → Task → SignalCatch → End (catches signal)
        var startB = new StartEvent("start");
        var taskB = new TaskActivity("task1");
        var signalCatch = new SignalIntermediateCatchEvent("waitApproval", "sig1");
        var endB = new EndEvent("end");

        var workflowB = new WorkflowDefinition
        {
            WorkflowId = "signal-catch-cross",
            Activities = [startB, taskB, signalCatch, endB],
            SequenceFlows =
            [
                new SequenceFlow("f1", startB, taskB),
                new SequenceFlow("f2", taskB, signalCatch),
                new SequenceFlow("f3", signalCatch, endB)
            ],
            Signals = [signalDef]
        };

        // Workflow A: Start → ThrowSignal → End (throws signal)
        var startA = new StartEvent("start");
        var throwSignal = new SignalIntermediateThrowEvent("emitApproval", "sig1");
        var endA = new EndEvent("end");

        var workflowA = new WorkflowDefinition
        {
            WorkflowId = "signal-throw-cross",
            Activities = [startA, throwSignal, endA],
            SequenceFlows =
            [
                new SequenceFlow("f1", startA, throwSignal),
                new SequenceFlow("f2", throwSignal, endA)
            ],
            Signals = [signalDef]
        };

        // Start Workflow B first — it suspends at catch
        var instanceB = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instanceB.SetWorkflow(workflowB);
        await instanceB.StartWorkflow();
        await instanceB.CompleteActivity("task1", new ExpandoObject());

        // Verify B is suspended at signal catch
        var snapB = await QueryService.GetStateSnapshot(instanceB.GetPrimaryKey());
        Assert.IsFalse(snapB!.IsCompleted, "Workflow B should be suspended at signal catch");
        Assert.IsTrue(snapB.ActiveActivities.Any(a => a.ActivityId == "waitApproval"),
            "Signal catch should be active in Workflow B");

        // Act — Start Workflow A (it throws signal and completes)
        var instanceA = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instanceA.SetWorkflow(workflowA);
        await instanceA.StartWorkflow();

        // Assert — both workflows should be completed
        var finalSnapA = await QueryService.GetStateSnapshot(instanceA.GetPrimaryKey());
        var finalSnapB = await QueryService.GetStateSnapshot(instanceB.GetPrimaryKey());
        Assert.IsTrue(finalSnapA!.IsCompleted, "Workflow A (throw) should be completed");
        Assert.IsTrue(finalSnapB!.IsCompleted, "Workflow B (catch) should be completed after signal delivery");
    }
}
