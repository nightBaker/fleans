using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// A signal name's subscriptions are spread over <see cref="SignalCorrelationOptions.BucketCount"/>
/// bucket grains by a stable hash of the workflow instance id; a broadcast reaches every bucket.
/// </summary>
[TestClass]
public class SignalBucketTests : WorkflowTestBase
{
    private const string CatchId = "waitSignal";
    private static readonly SignalCorrelationOptions Signals = new();

    [TestMethod]
    public async Task Subscriptions_OfManyInstances_LandInTheBucketOfTheirInstanceId()
    {
        // Arrange
        var signalName = UniqueSignal();
        var instanceIds = await StartWaitingInstances(signalName, count: 40);

        // Act — broadcast each bucket on its own
        var deliveredPerBucket = new int[Signals.BucketCount];
        for (var bucket = 0; bucket < Signals.BucketCount; bucket++)
            deliveredPerBucket[bucket] = await Cluster.GrainFactory
                .GetGrain<ISignalCorrelationGrain>(bucket, signalName).BroadcastSignal();

        // Assert — each bucket held exactly the instances that hash to it
        for (var bucket = 0; bucket < Signals.BucketCount; bucket++)
            Assert.AreEqual(instanceIds.Count(id => Signals.BucketFor(id) == bucket), deliveredPerBucket[bucket],
                $"bucket {bucket}");
        Assert.IsTrue(deliveredPerBucket.Count(n => n > 0) > 1, "40 instances should spread over several buckets");
        foreach (var id in instanceIds)
            await WaitForCondition(id, s => s.IsCompleted);
    }

    [TestMethod]
    public async Task Broadcast_ReachesEverySubscriber_AcrossBuckets()
    {
        // Arrange
        var signalName = UniqueSignal();
        var instanceIds = await StartWaitingInstances(signalName, count: 40);

        // Act
        var delivered = await BroadcastSignal(signalName);

        // Assert
        Assert.AreEqual(instanceIds.Count, delivered);
        foreach (var id in instanceIds)
            await WaitForCondition(id, s => s.IsCompleted);
        Assert.AreEqual(0, await BroadcastSignal(signalName), "a broadcast consumes the subscriptions");
    }

    [TestMethod]
    public async Task RedeliveredSubscribe_IsIdempotent()
    {
        // Arrange
        var signalName = UniqueSignal();
        var instanceId = (await StartWaitingInstances(signalName, count: 1)).Single();
        var hostId = await CatchActivityInstanceId(instanceId);
        var bucket = Cluster.GrainFactory.GetSignalCorrelationGrain(signalName, instanceId, Signals);

        // Act — the subscribe effect delivered twice more
        await bucket.Subscribe(instanceId, CatchId, hostId);
        await bucket.Subscribe(instanceId, CatchId, hostId);

        // Assert — one subscription, one delivery, completed once
        Assert.AreEqual(1, await BroadcastSignal(signalName));
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.AreEqual(1, snapshot.CompletedActivityIds.Count(a => a == CatchId));
    }

    [TestMethod]
    public async Task RedeliveredUnsubscribe_IsIdempotent()
    {
        // Arrange
        var signalName = UniqueSignal();
        var instanceId = (await StartWaitingInstances(signalName, count: 1)).Single();
        var hostId = await CatchActivityInstanceId(instanceId);
        var bucket = Cluster.GrainFactory.GetSignalCorrelationGrain(signalName, instanceId, Signals);

        // Act
        await bucket.Unsubscribe(instanceId, CatchId);
        await bucket.Unsubscribe(instanceId, CatchId);

        // Assert — nothing left to deliver, and the bucket still accepts a new subscription
        Assert.AreEqual(0, await BroadcastSignal(signalName));
        await bucket.Subscribe(instanceId, CatchId, hostId);
        Assert.AreEqual(1, await BroadcastSignal(signalName));
        await WaitForCondition(instanceId, s => s.IsCompleted);
    }

    private static string UniqueSignal() => "bucket-signal-" + Guid.NewGuid().ToString("N");

    // Start → Task → SignalCatch → End; completing the task subscribes the catch.
    private async Task<List<Guid>> StartWaitingInstances(string signalName, int count)
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalCatch = new SignalIntermediateCatchEvent(CatchId, "sig1");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-bucket-" + signalName,
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

    private async Task<Guid> CatchActivityInstanceId(Guid instanceId)
    {
        var snapshot = await WaitForCondition(instanceId, s => s.ActiveActivities.Any(a => a.ActivityId == CatchId));
        return snapshot.ActiveActivities.Single(a => a.ActivityId == CatchId).ActivityInstanceId;
    }
}
