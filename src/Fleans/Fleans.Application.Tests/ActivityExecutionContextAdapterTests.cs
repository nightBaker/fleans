using Fleans.Application.Adapters;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Application.Tests;

[TestClass]
public class ActivityExecutionContextAdapterTests
{
    private static readonly Guid InstanceId = Guid.NewGuid();
    private static readonly Guid WorkflowInstanceId = Guid.NewGuid();

    private static ActivityInstanceEntry CreateEntry(
        string activityId = "task1",
        Guid? scopeId = null,
        int? multiInstanceIndex = null)
    {
        var entry = multiInstanceIndex.HasValue
            ? new ActivityInstanceEntry(InstanceId, activityId, WorkflowInstanceId, scopeId, multiInstanceIndex.Value)
            : new ActivityInstanceEntry(InstanceId, activityId, WorkflowInstanceId, scopeId);

        entry.SetVariablesId(Guid.NewGuid());
        entry.SetTokenId(Guid.NewGuid());
        return entry;
    }

    private static ActivityExecutionContextAdapter CreateAdapter(ActivityInstanceEntry? entry = null)
        => new(entry ?? CreateEntry());

    // --- Constructor ---

    [TestMethod]
    public void Constructor_WithNullEntry_ShouldThrow()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new ActivityExecutionContextAdapter(null!));
    }

    // --- Read-only delegates ---

    [TestMethod]
    public async Task GetActivityInstanceId_ShouldReturnEntryValue()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.GetActivityInstanceId();

        Assert.AreEqual(entry.ActivityInstanceId, result);
    }

    [TestMethod]
    public async Task GetActivityId_ShouldReturnEntryValue()
    {
        var entry = CreateEntry("myActivity");
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.GetActivityId();

        Assert.AreEqual("myActivity", result);
    }

    [TestMethod]
    public async Task GetVariablesStateId_ShouldReturnEntryVariablesId()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.GetVariablesStateId();

        Assert.AreEqual(entry.VariablesId, result);
    }

    [TestMethod]
    public async Task GetMultiInstanceIndex_WhenNotSet_ShouldReturnNull()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.GetMultiInstanceIndex();

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetMultiInstanceIndex_WhenSet_ShouldReturnValue()
    {
        var entry = CreateEntry(multiInstanceIndex: 3);
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.GetMultiInstanceIndex();

        Assert.AreEqual(3, result);
    }

    [TestMethod]
    public async Task GetMultiInstanceTotal_WhenNotSet_ShouldReturnNull()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.GetMultiInstanceTotal();

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetMultiInstanceTotal_WhenSet_ShouldReturnValue()
    {
        var entry = CreateEntry();
        entry.SetMultiInstanceTotal(5);
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.GetMultiInstanceTotal();

        Assert.AreEqual(5, result);
    }

    [TestMethod]
    public async Task IsCompleted_WhenNotCompleted_ShouldReturnFalse()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.IsCompleted();

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task IsCompleted_WhenCompleted_ShouldReturnTrue()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Complete();
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.IsCompleted();

        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task GetTokenId_WhenSet_ShouldReturnValue()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.GetTokenId();

        Assert.IsNotNull(result);
        Assert.AreEqual(entry.TokenId, result);
    }

    [TestMethod]
    public async Task GetTokenId_WhenNotSet_ShouldReturnNull()
    {
        // Create entry without setting token
        var entry = new ActivityInstanceEntry(InstanceId, "task1", WorkflowInstanceId);
        var adapter = new ActivityExecutionContextAdapter(entry);

        var result = await adapter.GetTokenId();

        Assert.IsNull(result);
    }

    // --- State-changing methods ---

    [TestMethod]
    public async Task SetMultiInstanceTotal_ShouldDelegateToEntry()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        await adapter.SetMultiInstanceTotal(10);

        Assert.AreEqual(10, entry.MultiInstanceTotal);
    }

    [TestMethod]
    public async Task Complete_ShouldDelegateToEntryAndSetFlag()
    {
        var entry = CreateEntry();
        entry.Execute(); // must be executing before completing
        var adapter = new ActivityExecutionContextAdapter(entry);

        await adapter.Complete();

        Assert.IsTrue(entry.IsCompleted);
        Assert.IsTrue(adapter.WasCompleted);
    }

    [TestMethod]
    public async Task Complete_ShouldMakeIsCompletedReturnTrue()
    {
        var entry = CreateEntry();
        entry.Execute();
        var adapter = new ActivityExecutionContextAdapter(entry);

        Assert.IsFalse(await adapter.IsCompleted());

        await adapter.Complete();

        Assert.IsTrue(await adapter.IsCompleted());
    }

    [TestMethod]
    public async Task Execute_ShouldDelegateToEntryAndSetFlag()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        await adapter.Execute();

        Assert.IsTrue(entry.IsExecuting);
        Assert.IsTrue(adapter.WasExecuted);
    }

    // --- Flags ---

    [TestMethod]
    public void WasCompleted_InitiallyFalse()
    {
        var adapter = CreateAdapter();

        Assert.IsFalse(adapter.WasCompleted);
    }

    [TestMethod]
    public void WasExecuted_InitiallyFalse()
    {
        var adapter = CreateAdapter();

        Assert.IsFalse(adapter.WasExecuted);
    }

    [TestMethod]
    public async Task WasCompleted_TrueAfterComplete()
    {
        var entry = CreateEntry();
        entry.Execute();
        var adapter = new ActivityExecutionContextAdapter(entry);

        await adapter.Complete();

        Assert.IsTrue(adapter.WasCompleted);
    }

    [TestMethod]
    public async Task WasExecuted_TrueAfterExecute()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        await adapter.Execute();

        Assert.IsTrue(adapter.WasExecuted);
    }

    // --- PublishEvent ---

    [TestMethod]
    public void PublishedEvents_InitiallyEmpty()
    {
        var adapter = CreateAdapter();

        Assert.AreEqual(0, adapter.PublishedEvents.Count);
    }

    [TestMethod]
    public async Task PublishEvent_ShouldCollectEvents()
    {
        var adapter = CreateAdapter();
        var event1 = new WorkflowCompleted();
        var event2 = new ActivityExecutionStarted(Guid.NewGuid());

        await adapter.PublishEvent(event1);
        await adapter.PublishEvent(event2);

        Assert.AreEqual(2, adapter.PublishedEvents.Count);
        Assert.AreSame(event1, adapter.PublishedEvents[0]);
        Assert.AreSame(event2, adapter.PublishedEvents[1]);
    }

    [TestMethod]
    public async Task PublishEvent_WithNullEvent_ShouldThrow()
    {
        var adapter = CreateAdapter();

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => adapter.PublishEvent(null!).AsTask());
    }

    [TestMethod]
    public async Task PublishEvent_ShouldPreserveOrder()
    {
        var adapter = CreateAdapter();
        var events = Enumerable.Range(0, 5)
            .Select(i => new ActivityExecutionStarted(Guid.NewGuid()))
            .ToList();

        foreach (var evt in events)
            await adapter.PublishEvent(evt);

        Assert.AreEqual(5, adapter.PublishedEvents.Count);
        for (int i = 0; i < events.Count; i++)
            Assert.AreSame(events[i], adapter.PublishedEvents[i]);
    }

    [TestMethod]
    public void PublishedEvents_ShouldBeReadOnly()
    {
        var adapter = CreateAdapter();

        // IReadOnlyList does not expose Add — verify type at runtime
        Assert.IsInstanceOfType<IReadOnlyList<IDomainEvent>>(adapter.PublishedEvents);
    }

    // --- Full lifecycle scenario ---

    [TestMethod]
    public async Task FullLifecycle_ExecuteThenComplete_ShouldTrackAllState()
    {
        // Arrange
        var entry = CreateEntry("scriptTask1");
        var adapter = new ActivityExecutionContextAdapter(entry);

        // Assert initial state
        Assert.IsFalse(adapter.WasExecuted);
        Assert.IsFalse(adapter.WasCompleted);
        Assert.AreEqual(0, adapter.PublishedEvents.Count);
        Assert.IsFalse(await adapter.IsCompleted());

        // Act — execute
        await adapter.Execute();

        // Assert after execute
        Assert.IsTrue(adapter.WasExecuted);
        Assert.IsFalse(adapter.WasCompleted);
        Assert.IsTrue(entry.IsExecuting);
        Assert.IsFalse(await adapter.IsCompleted());

        // Act — publish event during execution
        var domainEvent = new WorkflowCompleted();
        await adapter.PublishEvent(domainEvent);

        // Act — complete
        await adapter.Complete();

        // Assert after complete
        Assert.IsTrue(adapter.WasCompleted);
        Assert.IsTrue(await adapter.IsCompleted());
        Assert.IsFalse(entry.IsExecuting); // executing cleared on complete
        Assert.AreEqual(1, adapter.PublishedEvents.Count);
        Assert.AreSame(domainEvent, adapter.PublishedEvents[0]);

        // Verify identity accessors still work
        Assert.AreEqual(entry.ActivityInstanceId, await adapter.GetActivityInstanceId());
        Assert.AreEqual("scriptTask1", await adapter.GetActivityId());
    }

    // --- Edge cases ---

    [TestMethod]
    public async Task Complete_WithoutExecute_ShouldSucceed()
    {
        // The entry's Complete() only checks IsCompleted, not IsExecuting.
        // Completing without executing first is allowed.
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);

        await adapter.Complete();

        Assert.IsTrue(adapter.WasCompleted);
        Assert.IsTrue(entry.IsCompleted);
    }

    [TestMethod]
    public async Task Execute_WhenAlreadyExecuting_ShouldThrowFromEntry()
    {
        var entry = CreateEntry();
        var adapter = new ActivityExecutionContextAdapter(entry);
        await adapter.Execute();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => adapter.Execute().AsTask());
    }

    [TestMethod]
    public async Task Complete_WhenAlreadyCompleted_ShouldThrowFromEntry()
    {
        var entry = CreateEntry();
        entry.Execute();
        var adapter = new ActivityExecutionContextAdapter(entry);
        await adapter.Complete();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => adapter.Complete().AsTask());
    }
}
