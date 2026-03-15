using Fleans.Domain.Poc;

namespace Fleans.Application.Tests.Poc;

[TestClass]
public class JournaledCounterTests : JournaledCounterTestBase
{
    [TestMethod]
    public async Task Increment_ThreeTimes_ValueEqualsSum()
    {
        // Arrange
        var grain = Cluster.GrainFactory.GetGrain<IJournaledCounterGrain>("test-1");

        // Act
        await grain.Increment(5);
        await grain.Increment(3);
        await grain.Increment(7);

        // Assert
        Assert.AreEqual(15, await grain.GetValue());
        Assert.AreEqual(3, await grain.GetVersion());
        Assert.AreEqual(3, await grain.GetEventCount());
    }

    [TestMethod]
    public async Task MultipleEventTypes_CorrectFinalState()
    {
        // Arrange
        var grain = Cluster.GrainFactory.GetGrain<IJournaledCounterGrain>("test-2");

        // Act
        await grain.Increment(10);
        await grain.Decrement(3);
        await grain.Increment(5);
        await grain.Reset();
        await grain.Increment(42);

        // Assert
        Assert.AreEqual(42, await grain.GetValue());
        Assert.AreEqual(5, await grain.GetVersion());
        Assert.AreEqual(5, await grain.GetEventCount());
    }

    [TestMethod]
    public async Task StateRecovery_AfterDeactivation_StateMatchesPreDeactivation()
    {
        // Arrange — perform a multi-operation sequence
        var grain = Cluster.GrainFactory.GetGrain<IJournaledCounterGrain>("test-3");
        await grain.Increment(5);
        await grain.Increment(3);
        await grain.Decrement(2);
        await grain.Reset();
        await grain.Increment(10);

        // Verify pre-deactivation state
        Assert.AreEqual(10, await grain.GetValue());
        Assert.AreEqual(5, await grain.GetVersion());
        Assert.AreEqual(5, await grain.GetEventCount());

        // Act — deactivate the grain via self-deactivation
        await grain.Deactivate();
        await Task.Delay(1000);

        // Assert — reactivate by calling a method, state should be restored from event replay
        var reactivated = Cluster.GrainFactory.GetGrain<IJournaledCounterGrain>("test-3");
        Assert.AreEqual(10, await reactivated.GetValue());
        Assert.AreEqual(5, await reactivated.GetVersion());
        Assert.AreEqual(5, await reactivated.GetEventCount());
    }

    [TestMethod]
    public async Task BatchRaiseEvent_SingleConfirmEvents_VersionIncrementsCorrectly()
    {
        // Arrange
        var grain = Cluster.GrainFactory.GetGrain<IJournaledCounterGrain>("test-4");

        // Act — each Increment drains one event via RaiseEvent + ConfirmEvents
        await grain.Increment(1);
        await grain.Increment(1);
        await grain.Increment(1);

        // Assert
        Assert.AreEqual(3, await grain.GetValue());
        Assert.AreEqual(3, await grain.GetVersion());
    }

    [TestMethod]
    public async Task VersionTracking_IncrementsAcrossOperations()
    {
        // Arrange
        var grain = Cluster.GrainFactory.GetGrain<IJournaledCounterGrain>("test-5");

        // Act & Assert — verify version increments after each operation
        await grain.Increment(1);
        Assert.AreEqual(1, await grain.GetVersion());

        await grain.Decrement(1);
        Assert.AreEqual(2, await grain.GetVersion());

        await grain.Reset();
        Assert.AreEqual(3, await grain.GetVersion());

        await grain.Increment(100);
        Assert.AreEqual(4, await grain.GetVersion());
        Assert.AreEqual(100, await grain.GetValue());
    }

    [TestMethod]
    public async Task DoubleApply_AggregateAndJournaledGrainStayInSync()
    {
        // This validates the "double-apply" concern: events are applied once by
        // the aggregate (for immediate consistency) and once by JournaledGrain
        // (for persistence/recovery). After deactivation+reactivation, the
        // JournaledGrain-replayed state should match what the aggregate had.
        var grain = Cluster.GrainFactory.GetGrain<IJournaledCounterGrain>("test-6");

        // Build up state through aggregate
        await grain.Increment(100);
        await grain.Decrement(30);
        await grain.Increment(5);
        // Expected: 100 - 30 + 5 = 75

        var valueBeforeDeactivation = await grain.GetValue();
        var versionBeforeDeactivation = await grain.GetVersion();
        var eventCountBeforeDeactivation = await grain.GetEventCount();

        // Deactivate to force state recovery via JournaledGrain event replay
        await grain.Deactivate();
        await Task.Delay(1000);

        // Reactivate and verify
        var reactivated = Cluster.GrainFactory.GetGrain<IJournaledCounterGrain>("test-6");
        Assert.AreEqual(valueBeforeDeactivation, await reactivated.GetValue());
        Assert.AreEqual(versionBeforeDeactivation, await reactivated.GetVersion());
        Assert.AreEqual(eventCountBeforeDeactivation, await reactivated.GetEventCount());

        // Continue operations after reactivation — should work seamlessly
        await reactivated.Increment(25);
        Assert.AreEqual(100, await reactivated.GetValue()); // 75 + 25
        Assert.AreEqual(4, await reactivated.GetVersion());
        Assert.AreEqual(4, await reactivated.GetEventCount());
    }
}
