using Fleans.Application.Abstractions.Events;
using Fleans.Application.Events;
using Fleans.Application.Grains;
using Fleans.Application.Tests.CustomTasks.TestStubs;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks;

/// <summary>
/// Verifies the per-task-type stream namespace contract from #566:
///   1. Publisher routes each <see cref="ExecuteCustomTaskEvent"/> to the per-<c>TaskType</c>
///      namespace, so plugins receive only their own traffic.
///   2. <c>AddCustomTaskPlugin&lt;T&gt;</c> throws on duplicate <c>TaskType</c>.
///   3. <c>AddCustomTaskPlugin&lt;T&gt;</c> throws when <typeparamref name="T"/>'s
///      <c>[ImplicitStreamSubscription]</c> string does not match its <c>TaskType</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CustomTaskRoutingTests : WorkflowTestBase
{
    [TestMethod]
    public async Task Per_type_namespace_is_used_when_publishing_custom_task_event()
    {
        // Two distinct per-type streams; subscribe a test-side observer to each
        // before publishing. Stream key matches what the publisher uses
        // (WorkflowInstanceId.ToString("D")) so observers see exactly the published event.
        //
        // WorkflowTestBase configures the stream provider on the silo, not on the test
        // cluster's client — resolve via the silo-side service helper that the base class
        // already provides.
        var instanceId = Guid.NewGuid();
        var siloServices = GetSiloService<IServiceProvider>();
        var streamProvider = siloServices.GetRequiredKeyedService<IStreamProvider>(WorkflowEventStreams.StreamProvider);

        var streamIdA = StreamId.Create(
            WorkflowEventStreams.GetExecuteCustomTaskNamespace("test-no-op-a"),
            instanceId.ToString("D"));
        var streamA = streamProvider.GetStream<ExecuteCustomTaskEvent>(streamIdA);
        var observerA = new CountingObserver();
        await streamA.SubscribeAsync(observerA);

        var streamIdB = StreamId.Create(
            WorkflowEventStreams.GetExecuteCustomTaskNamespace("test-no-op-b"),
            instanceId.ToString("D"));
        var streamB = streamProvider.GetStream<ExecuteCustomTaskEvent>(streamIdB);
        var observerB = new CountingObserver();
        await streamB.SubscribeAsync(observerB);

        // Publish a "test-no-op-a" event through the engine publisher.
        var publisher = Cluster.GrainFactory.GetGrain<IEventPublisher>(0);
        await publisher.Publish(MakeEvent(instanceId, taskType: "test-no-op-a"));

        // Wait for the memory-stream delivery to land. The observer is invoked on
        // a stream-provider thread, so we poll rather than assert immediately.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && observerA.Count == 0)
            await Task.Delay(50);

        Assert.AreEqual(1, observerA.Count,
            "The per-type stream for 'test-no-op-a' should receive exactly one event.");
        Assert.AreEqual(0, observerB.Count,
            "The per-type stream for 'test-no-op-b' must NOT receive a 'test-no-op-a' event " +
            "— this is the fanout-elimination invariant.");
    }

    [TestMethod]
    public void AddCustomTaskPlugin_throws_on_duplicate_TaskType()
    {
        var services = new ServiceCollection();
        services.AddCustomTaskPlugin<TestNoOpCustomTaskHandlerA>("test-no-op-a", "First");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddCustomTaskPlugin<TestNoOpCustomTaskHandlerB>("test-no-op-a", "Second"));

        StringAssert.Contains(ex.Message, "already registered");
        StringAssert.Contains(ex.Message, "test-no-op-a");
    }

    [TestMethod]
    public void AddCustomTaskPlugin_throws_when_attribute_namespace_drifts_from_TaskType()
    {
        var services = new ServiceCollection();

        // BadAttributeCustomTaskHandler declares
        //   [ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.wrong-name")]
        // but TaskType => "right-name" — the attribute string does not match the
        // namespace the publisher would route "right-name" events to.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddCustomTaskPlugin<BadAttributeCustomTaskHandler>("right-name", "Bad"));

        StringAssert.Contains(ex.Message, "events.ExecuteCustomTaskEvent.right-name");
        StringAssert.Contains(ex.Message, nameof(BadAttributeCustomTaskHandler));
    }

    [TestMethod]
    public void Handler_grain_type_is_named_after_its_task_type()
    {
        // Orleans' default grain type is the bare class name; the two SliceCustomTaskHandler
        // classes (one per plugin host, see HeterogeneousPluginHostsTests) would share it.
        var resolver = GetSiloService<GrainTypeResolver>();

        Assert.AreEqual("custom-task.test-no-op-a",
            resolver.GetGrainType(typeof(TestNoOpCustomTaskHandlerA)).ToString());
        Assert.AreEqual("custom-task.test-host-a",
            resolver.GetGrainType(typeof(TestStubs.HostA.SliceCustomTaskHandler)).ToString());
        Assert.AreEqual("custom-task.test-host-b",
            resolver.GetGrainType(typeof(TestStubs.HostB.SliceCustomTaskHandler)).ToString());
        Assert.IsFalse(resolver.GetGrainType(typeof(WorkflowInstance)).ToString().StartsWith("custom-task."),
            "Only custom-task handlers are renamed.");
    }

    [TestMethod]
    public void AddCustomTaskPlugin_registers_the_grain_type_provider_once()
    {
        var services = new ServiceCollection();

        services.AddCustomTaskPlugin<TestNoOpCustomTaskHandlerA>("test-no-op-a");
        services.AddCustomTaskPlugin<TestNoOpCustomTaskHandlerB>("test-no-op-b");

        Assert.AreEqual(1, services.Count(d => d.ServiceType == typeof(IGrainTypeProvider)));
    }

    [TestMethod]
    public void AddCustomTaskPlugin_rejects_a_slash_in_TaskType()
    {
        // The task type becomes part of the grain type, and '/' separates a GrainId's type from its key.
        var services = new ServiceCollection();

        Assert.ThrowsExactly<ArgumentException>(() =>
            services.AddCustomTaskPlugin<TestNoOpCustomTaskHandlerA>("crm/sync"));
    }

    private static ExecuteCustomTaskEvent MakeEvent(Guid workflowInstanceId, string taskType) =>
        new(
            WorkflowInstanceId: workflowInstanceId,
            WorkflowId: "test-workflow",
            ProcessDefinitionId: null,
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: "test-activity",
            TaskType: taskType,
            Inputs: new System.Dynamic.ExpandoObject(),
            VariablesId: Guid.NewGuid(),
            BusinessId: null);

    private sealed class CountingObserver : IAsyncObserver<ExecuteCustomTaskEvent>
    {
        private int _count;
        public int Count => _count;
        public Task OnNextAsync(ExecuteCustomTaskEvent item, StreamSequenceToken? token = null)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
        public Task OnCompletedAsync() => Task.CompletedTask;
        public Task OnErrorAsync(Exception ex) => Task.CompletedTask;
    }
}
