using Fleans.Application.Events.Handlers;
using Fleans.Application.Grains;
using Fleans.Domain.Events;

namespace Fleans.Application.Tests.Streaming;

/// <summary>
/// Pins the invariant that <see cref="WorkflowExecuteScriptEventHandler"/> (and by symmetry
/// every <c>[ImplicitStreamSubscription]</c> handler in the engine) sees exactly one
/// subscription handle on activation. We rely on this invariant when dropping the
/// <c>SubscribeAsync</c> fallback in <c>OnActivateAsync</c> — without a handle, the grain
/// would silently never fire.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ImplicitSubscriptionHandleTest : WorkflowTestBase
{
    private TaskCompletionSource<int> _tcs = null!;

    [TestInitialize]
    public void HookInit()
    {
        _tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowExecuteScriptEventHandler.OnImplicitActivation = c => _tcs.TrySetResult(c);
    }

    [TestCleanup]
    public void HookCleanup() =>
        WorkflowExecuteScriptEventHandler.OnImplicitActivation = null;

    [TestMethod]
    public async Task Publishing_one_event_yields_exactly_one_subscription_handle()
    {
        var publisher = Cluster.GrainFactory.GetGrain<IEventPublisher>(0);

        await publisher.Publish(new ExecuteScriptEvent(
            WorkflowInstanceId: Guid.NewGuid(),
            WorkflowId: "test-workflow",
            ProcessDefinitionId: null,
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: "test-activity",
            Script: "return null;",
            ScriptFormat: "csharp",
            VariablesId: Guid.NewGuid()));

        var count = await _tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, count,
            "Implicit subscription should produce exactly one handle on the activating grain.");
    }
}
