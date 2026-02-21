using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalIntermediateCatchEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task SignalCatch_ShouldSuspendWorkflow_UntilSignalBroadcast()
    {
        // Arrange — Start → Task → SignalCatch → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "orderApproved");
        var signalCatch = new SignalIntermediateCatchEvent("waitApproval", "sig1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-catch-test",
            Activities = [start, task, signalCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, signalCatch),
                new SequenceFlow("f3", signalCatch, end)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — workflow suspended at signal catch
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should NOT be completed — waiting for signal");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "waitApproval"),
            "Signal catch activity should be active");

        // Act — broadcast signal via correlation grain
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("orderApproved");
        var deliveredCount = await signalGrain.BroadcastSignal();

        // Assert — workflow completed
        Assert.AreEqual(1, deliveredCount, "Signal should be delivered to one subscriber");
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after signal broadcast");
    }

    [TestMethod]
    public async Task SignalCatch_MultipleSubscribers_AllShouldReceive()
    {
        // Arrange — two workflows waiting for the same signal
        var signalDef = new SignalDefinition("sig1", "orderApproved");

        WorkflowDefinition CreateWorkflow(string id)
        {
            var start = new StartEvent("start");
            var task = new TaskActivity("task1");
            var signalCatch = new SignalIntermediateCatchEvent("waitApproval", "sig1");
            var end = new EndEvent("end");

            return new WorkflowDefinition
            {
                WorkflowId = id,
                Activities = [start, task, signalCatch, end],
                SequenceFlows =
                [
                    new SequenceFlow("f1", start, task),
                    new SequenceFlow("f2", task, signalCatch),
                    new SequenceFlow("f3", signalCatch, end)
                ],
                Signals = [signalDef]
            };
        }

        // Instance 1
        var instance1 = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance1.SetWorkflow(CreateWorkflow("signal-multi-1"));
        await instance1.StartWorkflow();
        await instance1.CompleteActivity("task1", new ExpandoObject());

        // Instance 2
        var instance2 = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance2.SetWorkflow(CreateWorkflow("signal-multi-2"));
        await instance2.StartWorkflow();
        await instance2.CompleteActivity("task1", new ExpandoObject());

        // Verify both are suspended at signal catch
        var snap1 = await QueryService.GetStateSnapshot(instance1.GetPrimaryKey());
        var snap2 = await QueryService.GetStateSnapshot(instance2.GetPrimaryKey());
        Assert.IsTrue(snap1!.ActiveActivities.Any(a => a.ActivityId == "waitApproval"),
            "Instance 1 should be waiting at signal catch");
        Assert.IsTrue(snap2!.ActiveActivities.Any(a => a.ActivityId == "waitApproval"),
            "Instance 2 should be waiting at signal catch");

        // Act — broadcast signal once
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("orderApproved");
        var deliveredCount = await signalGrain.BroadcastSignal();

        // Assert — both workflows completed
        Assert.AreEqual(2, deliveredCount, "Signal should be delivered to both subscribers");
        var finalSnap1 = await QueryService.GetStateSnapshot(instance1.GetPrimaryKey());
        var finalSnap2 = await QueryService.GetStateSnapshot(instance2.GetPrimaryKey());
        Assert.IsTrue(finalSnap1!.IsCompleted, "Instance 1 should be completed after signal broadcast");
        Assert.IsTrue(finalSnap2!.IsCompleted, "Instance 2 should be completed after signal broadcast");
    }

    [TestMethod]
    public async Task SignalBroadcast_NoSubscribers_ShouldReturnZero()
    {
        // Act — broadcast to a signal with no subscribers
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("nonExistentSignal");
        var deliveredCount = await signalGrain.BroadcastSignal();

        // Assert
        Assert.AreEqual(0, deliveredCount, "Should return 0 when no subscribers exist");
    }
}
