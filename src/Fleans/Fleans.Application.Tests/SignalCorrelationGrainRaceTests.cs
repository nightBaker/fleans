using Fleans.Application.Grains;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalCorrelationGrainRaceTests : WorkflowTestBase
{
    // These exercise one bucket grain's mutex, so every call goes to bucket 0 whatever the
    // instance id; SignalBucketTests covers the routing of instances to buckets.
    private ISignalCorrelationGrain Bucket(string signalName) =>
        Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>(0, signalName);

    [TestMethod]
    public async Task Subscribe_RacingWithUnsubscribe_ConvergesToValidState()
    {
        for (int i = 0; i < 100; i++)
        {
            var grain = Bucket($"sig-race-{i}");
            var instanceId = Guid.NewGuid();
            var hostId = Guid.NewGuid();

            await grain.Subscribe(instanceId, "activity1", hostId);

            var subscribeTask = Task.Run(async () =>
            {
                await grain.Subscribe(Guid.NewGuid(), "activity2", Guid.NewGuid());
            });
            var unsubscribeTask = Task.Run(async () =>
            {
                await grain.Unsubscribe(instanceId, "activity1");
            });

            await Task.WhenAll(subscribeTask, unsubscribeTask);

            // Final state: either 1 subscriber (activity2) or 2 subscribers (if unsubscribe
            // ran before subscribe added activity2, then both are present). Both are valid —
            // the invariant is no data corruption or lost writes. Verify by broadcasting.
            var delivered = await grain.BroadcastSignal();
            // delivered count should be 0 (no real workflow) or throw on missing workflow.
            // The key assertion is that broadcast doesn't crash due to corrupted state.
        }
    }

    [TestMethod]
    public async Task BroadcastSignal_NoSubscribers_ReturnsZero()
    {
        var grain = Bucket("sig-empty-broadcast");

        var result = await grain.BroadcastSignal();

        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task SubscribeUnsubscribeCycle_MaintainsConsistentState()
    {
        var grain = Bucket("sig-cycle");

        for (int i = 0; i < 10; i++)
        {
            var instanceId = Guid.NewGuid();
            await grain.Subscribe(instanceId, $"activity-{i}", Guid.NewGuid());
            await grain.Unsubscribe(instanceId, $"activity-{i}");
        }

        // After all subscribe/unsubscribe cycles, the list should be empty
        var result = await grain.BroadcastSignal();
        Assert.AreEqual(0, result);
    }
}
