using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalBoundaryEventTests : BoundaryEventTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new SignalBoundaryEvent(boundaryId, attachedToId, "sig1", IsInterrupting: isInterrupting);

    protected override List<SignalDefinition> GetSignalDefinitions()
        => [new SignalDefinition("sig1", "cancelOrder")];

    protected override async Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId)
    {
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("cancelOrder");
        var deliveredCount = await signalGrain.BroadcastSignal();
        Assert.AreEqual(1, deliveredCount, "Signal should be delivered");
    }

    [TestMethod]
    public async Task BoundarySignal_StaleSignal_ShouldBeSilentlyIgnored()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "cancelOrder");
        var boundarySignal = new SignalBoundaryEvent("bsig1", "task1", "sig1");
        var end = new EndEvent("end");
        var sigEnd = new EndEvent("sigEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-signal-stale",
            Activities = [start, task, boundarySignal, end, sigEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundarySignal, sigEnd)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.IsCompleted);

        await workflowInstance.HandleBoundarySignalFired("bsig1", hostInstanceId);

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
    }
}
