using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// A broadcast hands each bucket's deliveries to the bucket's <c>events.SignalDelivery</c> stream,
/// and the bucket's delivery grain signals the instances at a bounded pace (#976).
/// </summary>
[TestClass]
public class SignalStreamDeliveryTests : WorkflowTestBase
{
    private const string CatchId = "waitSignal";

    [TestMethod]
    public async Task Broadcast_ThroughTheStream_ReachesEverySubscriber()
    {
        // Arrange
        var signalName = UniqueSignal();
        var instanceIds = await StartWaitingInstances(signalName, count: 40);

        // Act
        var handedOff = await BroadcastSignal(signalName);

        // Assert — every subscriber was handed to the stream, and every instance received it
        Assert.AreEqual(instanceIds.Count, handedOff);
        foreach (var id in instanceIds)
            await WaitForCondition(id, s => s.IsCompleted);
        Assert.AreEqual(0, await BroadcastSignal(signalName), "a broadcast consumes the subscriptions");
    }

    // #742 on the stream path: the consumer puts the subscription back instead of holding the
    // bucket's later pages behind an unreachable instance.
    [TestMethod]
    public async Task TransientDeliveryFailure_KeepsSubscription_ForTheNextBroadcast()
    {
        // Arrange
        var signalName = UniqueSignal();
        var instanceId = (await StartWaitingInstances(signalName, count: 1)).Single();
        SignalDeliveryFaults.FailNextDeliveries(instanceId, 1);

        // Act
        var handedOff = await BroadcastSignal(signalName);

        // Assert — handed off but not delivered; a later broadcast reaches it once it is restored
        Assert.AreEqual(1, handedOff);
        await Task.Delay(1000);
        var waiting = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(waiting!.ActiveActivities.Any(a => a.ActivityId == CatchId));
        Assert.AreEqual(1, await BroadcastSignalWhenSubscribed(signalName));
        await WaitForCondition(instanceId, s => s.IsCompleted);
    }

    // A silo that dies mid-page, or a failed restore, makes the stream redeliver the whole page:
    // the items already delivered must not be applied again.
    [TestMethod]
    public async Task RedeliveredPage_DeliversEachItemOnce()
    {
        // Arrange — a page whose single item fails transiently and whose restore succeeds is
        // indistinguishable from a redelivery only through its op-id, so replay the page by hand
        var signalName = UniqueSignal();
        var instanceId = (await StartWaitingInstances(signalName, count: 1)).Single();
        var hostId = (await WaitForCondition(instanceId, s => s.ActiveActivities.Any(a => a.ActivityId == CatchId)))
            .ActiveActivities.Single(a => a.ActivityId == CatchId).ActivityInstanceId;
        var page = new Fleans.Domain.Events.SignalDeliveryPage(signalName, 0,
            [new Fleans.Domain.Events.SignalDeliveryItem(instanceId, CatchId, hostId, "signal:" + Guid.NewGuid().ToString("N"))]);
        var stream = GetSiloService<IClusterClient>().GetStreamProvider(Abstractions.Events.WorkflowEventStreams.StreamProvider)
            .GetStream<Fleans.Domain.Events.SignalDeliveryPage>(Orleans.Runtime.StreamId.Create(
                Abstractions.Events.WorkflowEventStreams.SignalDeliveryStreamNamespace,
                Abstractions.Events.WorkflowEventStreams.SignalDeliveryStreamKey(0, signalName)));

        // Act — the same page arrives twice
        await stream.OnNextAsync(page);
        await stream.OnNextAsync(page);

        // Assert — delivered once
        var completed = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.AreEqual(1, completed.CompletedActivityIds.Count(a => a == CatchId));
    }

    [TestMethod]
    public async Task NonTransientDeliveryFailure_IsNotRetried()
    {
        // Arrange
        var signalName = UniqueSignal();
        var instanceId = (await StartWaitingInstances(signalName, count: 1)).Single();
        SignalDeliveryFaults.FailNextDeliveries(instanceId, 1, () => new InvalidOperationException("rejected"));

        // Act
        Assert.AreEqual(1, await BroadcastSignal(signalName));

        // Assert — a rejected delivery is dropped, not put back: the instance keeps waiting
        await Task.Delay(2000);
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted);
        Assert.AreEqual(0, await BroadcastSignal(signalName));
    }

    // A stream redelivers when the delivery call failed after the instance had queued it, so the
    // instance must apply one delivery once: a non-interrupting boundary would otherwise fire twice.
    [TestMethod]
    public async Task RedeliveredSignal_WithTheSameOperationId_FiresANonInterruptingBoundaryOnce()
    {
        // Arrange — task1 with a non-interrupting signal boundary leading to its own end
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = new SignalBoundaryEvent("bsig1", "task1", "sig1", IsInterrupting: false);
        var end = new EndEvent("end");
        var sigEnd = new EndEvent("sigEnd");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-redelivery-" + Guid.NewGuid().ToString("N"),
            Activities = [start, task, boundary, end, sigEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, sigEnd)
            ],
            Signals = [new SignalDefinition("sig1", UniqueSignal())]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        var instanceId = instance.GetPrimaryKey();
        var snapshot = await WaitForCondition(instanceId, s => s.ActiveActivities.Any(a => a.ActivityId == "task1"));
        var hostId = snapshot.ActiveActivities.Single(a => a.ActivityId == "task1").ActivityInstanceId;
        var operationId = "signal:" + Guid.NewGuid().ToString("N");

        // Act — the same delivery arrives twice
        await instance.HandleSignalDelivery(operationId, "bsig1", hostId);
        await instance.HandleSignalDelivery(operationId, "bsig1", hostId);
        await instance.CompleteActivity("task1", new ExpandoObject());

        // Assert — the boundary path ran once
        var completed = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.AreEqual(1, completed.CompletedActivityIds.Count(a => a == "sigEnd"));
    }

    private static string UniqueSignal() => "stream-signal-" + Guid.NewGuid().ToString("N");

    // Start → Task → SignalCatch → End; completing the task subscribes the catch.
    private async Task<List<Guid>> StartWaitingInstances(string signalName, int count)
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalCatch = new SignalIntermediateCatchEvent(CatchId, "sig1");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-stream-" + signalName,
            Activities = [start, task, signalCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, signalCatch),
                new SequenceFlow("f3", signalCatch, end)
            ],
            Signals = [new SignalDefinition("sig1", signalName)]
        };

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await instance.StartWorkflow(workflow);
            await instance.CompleteActivity("task1", new ExpandoObject());
            ids.Add(instance.GetPrimaryKey());
        }
        return ids;
    }
}
