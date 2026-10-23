using System.Collections.Concurrent;
using System.Dynamic;
using System.Reflection;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Concurrency;
using Orleans.TestingHost;

namespace Fleans.Application.Tests;

/// <summary>
/// Start-event listener grains are keyed by event name; their fire methods are [AlwaysInterleave]
/// so a slow instance start does not queue every other start of the same event behind it (#858).
/// A grain call filter holds every call into a new <c>WorkflowInstance</c> made under a test's
/// gate until <c>expected</c> distinct instances have been reached. If the listener serialized its
/// fire calls, the second fire could never reach its instance and the gate would never open.
/// </summary>
[TestClass]
public class StartEventListenerInterleavingTests : WorkflowTestBase
{
    private const string GateKey = "fleans-test-start-gate";
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<string, StartGate> Gates = new();

    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<StartGateSiloConfigurator>();

    [TestMethod]
    public async Task FireMessageStartEvent_ConcurrentFires_OverlapAndEachCreateOneInstance()
    {
        // Arrange
        var messageName = $"interleave-msg-{Guid.NewGuid():N}";
        var msgStart = new MessageStartEvent("msgStart", "msg1");
        var task = new TaskActivity("task1");
        await Deploy(new WorkflowDefinition
        {
            WorkflowId = $"interleave-msg-process-{Guid.NewGuid():N}",
            Activities = [msgStart, task],
            SequenceFlows = [new SequenceFlow("f1", msgStart, task)],
            Messages = [new MessageDefinition("msg1", messageName, null)]
        });
        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>(messageName);

        // Act
        var results = await FireConcurrentlyThroughGate(2, () => listener.FireMessageStartEvent(new ExpandoObject()).AsTask());

        // Assert
        AssertOneDistinctInstancePerFire(results.Select(r => r.ToList()).ToList());
    }

    [TestMethod]
    public async Task FireSignalStartEvent_ConcurrentFires_OverlapAndEachCreateOneInstance()
    {
        // Arrange
        var signalName = $"interleave-sig-{Guid.NewGuid():N}";
        var sigStart = new SignalStartEvent("sigStart", "sig1");
        var task = new TaskActivity("task1");
        await Deploy(new WorkflowDefinition
        {
            WorkflowId = $"interleave-sig-process-{Guid.NewGuid():N}",
            Activities = [sigStart, task],
            SequenceFlows = [new SequenceFlow("f1", sigStart, task)],
            Signals = [new SignalDefinition("sig1", signalName)]
        });
        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>(signalName);

        // Act
        var results = await FireConcurrentlyThroughGate(2, () => listener.FireSignalStartEvent().AsTask());

        // Assert
        AssertOneDistinctInstancePerFire(results.Select(r => r.ToList()).ToList());
    }

    [TestMethod]
    public async Task EvaluateAndStart_ConcurrentEvaluations_OverlapAndEachCreateOneInstance()
    {
        // Arrange
        var processId = $"interleave-cond-process-{Guid.NewGuid():N}";
        var condStart = new ConditionalStartEvent("condStart", "true");
        var task = new TaskActivity("task1");
        await Deploy(new WorkflowDefinition
        {
            WorkflowId = processId,
            Activities = [condStart, task],
            SequenceFlows = [new SequenceFlow("f1", condStart, task)]
        });
        var listener = Cluster.GrainFactory.GetGrain<IConditionalStartEventListenerGrain>($"{processId}:condStart");

        // Act
        var results = await FireConcurrentlyThroughGate(2, () => listener.EvaluateAndStart(new ExpandoObject()).AsTask());

        // Assert
        AssertOneDistinctInstancePerFire(results.Select(id => id is null ? new List<Guid>() : [id.Value]).ToList());
    }

    [TestMethod]
    public void ListenerInterfaces_InterleaveOnlyTheFireMethods()
    {
        // Register/Unregister mutate and persist the listener state, so they must stay serialized
        // with each other; only the fire methods, which copy the state before their first await,
        // may interleave.
        AssertInterleaving(typeof(IMessageStartEventListenerGrain), nameof(IMessageStartEventListenerGrain.FireMessageStartEvent));
        AssertInterleaving(typeof(ISignalStartEventListenerGrain), nameof(ISignalStartEventListenerGrain.FireSignalStartEvent));
        AssertInterleaving(typeof(IConditionalStartEventListenerGrain), nameof(IConditionalStartEventListenerGrain.EvaluateAndStart));

        foreach (var grainClass in new[]
                 {
                     typeof(MessageStartEventListenerGrain),
                     typeof(SignalStartEventListenerGrain),
                     typeof(ConditionalStartEventListenerGrain)
                 })
        {
            Assert.IsNull(grainClass.GetCustomAttribute<ReentrantAttribute>(inherit: true),
                $"{grainClass.Name} must not be [Reentrant]: Register/Unregister stay serialized.");
        }
    }

    private static void AssertInterleaving(Type grainInterface, string fireMethod)
    {
        foreach (var method in grainInterface.GetMethods())
        {
            var interleaves = method.GetCustomAttribute<AlwaysInterleaveAttribute>() is not null;
            Assert.AreEqual(method.Name == fireMethod, interleaves,
                $"{grainInterface.Name}.{method.Name}: only {fireMethod} may be [AlwaysInterleave].");
        }
    }

    private async Task Deploy(WorkflowDefinition workflow)
    {
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId);
        await processGrain.DeployVersion(workflow, "<placeholder/>");
    }

    private static async Task<T[]> FireConcurrentlyThroughGate<T>(int fires, Func<Task<T>> fire)
    {
        var gateId = Guid.NewGuid().ToString("N");
        var gate = new StartGate(fires);
        Gates[gateId] = gate;
        RequestContext.Set(GateKey, gateId);
        try
        {
            var calls = Enumerable.Range(0, fires).Select(_ => fire()).ToList();
            return await Task.WhenAll(calls).WaitAsync(GateTimeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"{fires} concurrent fires did not all reach their new instance within {GateTimeout}: " +
                "the listener serialized them.");
            throw;
        }
        finally
        {
            RequestContext.Remove(GateKey);
            gate.Open();
            Gates.TryRemove(gateId, out _);
        }
    }

    private static void AssertOneDistinctInstancePerFire(List<List<Guid>> perFire)
    {
        Assert.IsTrue(perFire.All(ids => ids.Count == 1),
            $"Each fire should create exactly one instance; got [{string.Join(", ", perFire.Select(ids => ids.Count))}].");
        var all = perFire.SelectMany(ids => ids).ToList();
        Assert.HasCount(all.Count, all.Distinct().ToList());
    }

    /// <summary>Opens once <c>expected</c> distinct instances have received a gated call.</summary>
    private sealed class StartGate(int expected)
    {
        private readonly HashSet<GrainId> _arrived = [];
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Arrive(GrainId instance)
        {
            lock (_arrived)
            {
                _arrived.Add(instance);
                if (_arrived.Count >= expected)
                    _open.TrySetResult();
            }
            return _open.Task;
        }

        public void Open() => _open.TrySetResult();
    }

    private sealed class StartGateFilter : IIncomingGrainCallFilter
    {
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.Grain is IWorkflowInstanceGrain
                && RequestContext.Get(GateKey) is string gateId
                && Gates.TryGetValue(gateId, out var gate))
            {
                await gate.Arrive(context.TargetId);
            }

            await context.Invoke();
        }
    }

    private sealed class StartGateSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) => hostBuilder.AddIncomingGrainCallFilter<StartGateFilter>();
    }
}
