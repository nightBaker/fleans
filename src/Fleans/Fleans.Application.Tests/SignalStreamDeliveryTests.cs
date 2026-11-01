using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// A bucket with more subscribers than <see cref="SignalCorrelationOptions.StreamDeliveryThreshold"/>
/// hands its deliveries to the <c>events.SignalDelivery</c> stream instead of calling every
/// instance from its own turn (#976). The threshold is 0 here, so every non-empty bucket streams.
/// </summary>
[TestClass]
public class SignalStreamDeliveryTests : WorkflowTestBase
{
    private const string CatchId = "waitSignal";

    protected override void ConfigureCluster(TestClusterBuilder builder) =>
        builder.AddSiloBuilderConfigurator<StreamEveryBucketConfigurator>();

    private sealed class StreamEveryBucketConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder.ConfigureServices(services =>
                services.Configure<SignalCorrelationOptions>(o => o.StreamDeliveryThreshold = 0));
    }

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

    [TestMethod]
    public async Task TransientDeliveryFailure_IsRedeliveredByTheStream()
    {
        // Arrange
        var signalName = UniqueSignal();
        var instanceId = (await StartWaitingInstances(signalName, count: 1)).Single();
        SignalDeliveryFaults.FailNextDeliveries(instanceId, 2);

        // Act
        var handedOff = await BroadcastSignal(signalName);

        // Assert — the stream retries the delivery of this broadcast; no second broadcast needed
        Assert.AreEqual(1, handedOff);
        await WaitForCondition(instanceId, s => s.IsCompleted, timeoutMs: 30000);
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

        // Assert — a rejected delivery is dropped, like on the direct path: the instance keeps waiting
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
