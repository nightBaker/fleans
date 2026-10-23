using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalCorrelationStateTests
{
    private static SignalSubscription Sub(Guid instanceId, string activityId)
        => new(instanceId, activityId, Guid.NewGuid());

    [TestMethod]
    public void AddSubscription_RecordsItInChangeSet()
    {
        var state = new SignalCorrelationState();
        var sub = Sub(Guid.NewGuid(), "a");

        Assert.IsTrue(state.AddSubscription(sub));

        CollectionAssert.AreEqual(new[] { sub }, state.Subscriptions.ToList());
        CollectionAssert.AreEqual(new[] { sub }, state.AddedSubscriptions.ToList());
        Assert.AreEqual(0, state.RemovedSubscriptionKeys.Count);
        Assert.IsFalse(state.SubscriptionsCleared);
    }

    [TestMethod]
    public void AddSubscription_DuplicateKey_ReturnsFalseAndChangesNothing()
    {
        var state = new SignalCorrelationState();
        var instanceId = Guid.NewGuid();
        state.AddSubscription(Sub(instanceId, "a"));
        state.AcceptChanges();

        Assert.IsFalse(state.AddSubscription(Sub(instanceId, "a")));

        Assert.AreEqual(1, state.Subscriptions.Count);
        Assert.AreEqual(0, state.AddedSubscriptions.Count);
    }

    [TestMethod]
    public void RemoveSubscription_RecordsKey()
    {
        var state = new SignalCorrelationState();
        var instanceId = Guid.NewGuid();
        state.AddSubscription(Sub(instanceId, "a"));
        state.AcceptChanges();

        Assert.IsTrue(state.RemoveSubscription(instanceId, "a"));

        Assert.AreEqual(0, state.Subscriptions.Count);
        CollectionAssert.AreEqual(new[] { (instanceId, "a") }, state.RemovedSubscriptionKeys.ToList());
    }

    [TestMethod]
    public void RemoveSubscription_Unknown_ReturnsFalseAndRecordsNothing()
    {
        var state = new SignalCorrelationState();

        Assert.IsFalse(state.RemoveSubscription(Guid.NewGuid(), "a"));

        Assert.AreEqual(0, state.RemovedSubscriptionKeys.Count);
    }

    [TestMethod]
    public void RemoveSubscription_OfUnwrittenAdd_DropsTheAdd()
    {
        var state = new SignalCorrelationState();
        var instanceId = Guid.NewGuid();
        state.AddSubscription(Sub(instanceId, "a"));

        state.RemoveSubscription(instanceId, "a");

        Assert.AreEqual(0, state.AddedSubscriptions.Count);
    }

    [TestMethod]
    public void TakeAllSubscriptions_ReturnsAllAndMarksCleared()
    {
        var state = new SignalCorrelationState();
        state.AddSubscription(Sub(Guid.NewGuid(), "a"));
        state.AddSubscription(Sub(Guid.NewGuid(), "b"));
        state.AcceptChanges();
        state.RemoveSubscription(state.Subscriptions[0].WorkflowInstanceId, "a");

        var taken = state.TakeAllSubscriptions();

        Assert.AreEqual(1, taken.Count);
        Assert.AreEqual(0, state.Subscriptions.Count);
        Assert.IsTrue(state.SubscriptionsCleared);
        Assert.AreEqual(0, state.AddedSubscriptions.Count);
        Assert.AreEqual(0, state.RemovedSubscriptionKeys.Count);
    }

    [TestMethod]
    public void AcceptChanges_ForgetsChangeSet_KeepsSubscriptions()
    {
        var state = new SignalCorrelationState();
        state.AddSubscription(Sub(Guid.NewGuid(), "a"));
        state.TakeAllSubscriptions();
        state.AddSubscription(Sub(Guid.NewGuid(), "b"));

        state.AcceptChanges();

        Assert.AreEqual(1, state.Subscriptions.Count);
        Assert.AreEqual(0, state.AddedSubscriptions.Count);
        Assert.AreEqual(0, state.RemovedSubscriptionKeys.Count);
        Assert.IsFalse(state.SubscriptionsCleared);
    }
}
