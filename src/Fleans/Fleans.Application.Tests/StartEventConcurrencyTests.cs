using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.TestingHost;

namespace Fleans.Application.Tests;

/// <summary>
/// Message, signal and conditional starts take the subscribed processes from the host's cache and go
/// straight to the new <c>WorkflowInstance</c> (#858): starts of one event run in parallel, and no
/// listener or registry grain is on the way. A grain call filter holds every call into a new
/// <c>WorkflowInstance</c> made under a test's gate until <c>expected</c> distinct instances have
/// been reached — if the starts were serialized, the second could never reach its instance and the
/// gate would never open — and records every call to a listener or registry grain under the gate.
/// </summary>
[TestClass]
public class StartEventConcurrencyTests : WorkflowTestBase
{
    private const string GateKey = "fleans-test-start-gate";
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<string, StartGate> Gates = new();

    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<StartGateSiloConfigurator>();

    [TestMethod]
    public async Task StartByMessage_ConcurrentStarts_OverlapAndSkipTheListenerGrain()
    {
        // Arrange
        var messageName = $"concurrent-msg-{Guid.NewGuid():N}";
        var msgStart = new MessageStartEvent("msgStart", "msg1");
        var task = new TaskActivity("task1");
        await Deploy(new WorkflowDefinition
        {
            WorkflowId = $"concurrent-msg-process-{Guid.NewGuid():N}",
            Activities = [msgStart, task],
            SequenceFlows = [new SequenceFlow("f1", msgStart, task)],
            Messages = [new MessageDefinition("msg1", messageName, null)]
        });
        Assert.HasCount(1, await StartEvents.StartByMessage(messageName, new ExpandoObject()), "warm-up start");

        // Act
        var (results, gate) = await StartConcurrentlyThroughGate(2,
            () => StartEvents.StartByMessage(messageName, new ExpandoObject()));

        // Assert
        AssertOneDistinctInstancePerStart(results.ToList());
        Assert.IsEmpty(gate.RegistrationCalls, $"Starts called a registration grain: {string.Join(", ", gate.RegistrationCalls)}");
    }

    [TestMethod]
    public async Task StartBySignal_ConcurrentStarts_OverlapAndSkipTheListenerGrain()
    {
        // Arrange
        var signalName = $"concurrent-sig-{Guid.NewGuid():N}";
        var sigStart = new SignalStartEvent("sigStart", "sig1");
        var task = new TaskActivity("task1");
        await Deploy(new WorkflowDefinition
        {
            WorkflowId = $"concurrent-sig-process-{Guid.NewGuid():N}",
            Activities = [sigStart, task],
            SequenceFlows = [new SequenceFlow("f1", sigStart, task)],
            Signals = [new SignalDefinition("sig1", signalName)]
        });
        Assert.HasCount(1, await StartEvents.StartBySignal(signalName), "warm-up start");

        // Act
        var (results, gate) = await StartConcurrentlyThroughGate(2, () => StartEvents.StartBySignal(signalName));

        // Assert
        AssertOneDistinctInstancePerStart(results.ToList());
        Assert.IsEmpty(gate.RegistrationCalls, $"Starts called a registration grain: {string.Join(", ", gate.RegistrationCalls)}");
    }

    [TestMethod]
    public async Task StartByConditions_ConcurrentStarts_OverlapAndSkipTheRegistryGrain()
    {
        // Arrange
        var processId = $"concurrent-cond-process-{Guid.NewGuid():N}";
        var condStart = new ConditionalStartEvent("condStart", "true");
        var task = new TaskActivity("task1");
        await Deploy(new WorkflowDefinition
        {
            WorkflowId = processId,
            Activities = [condStart, task],
            SequenceFlows = [new SequenceFlow("f1", condStart, task)]
        });
        Assert.IsNotNull(await EvaluateConditionalStart(processId, new ExpandoObject()), "warm-up start");

        // Act
        var (results, gate) = await StartConcurrentlyThroughGate(2,
            async () => (await StartEvents.StartByConditions(processId, new ExpandoObject())).StartedInstanceIds);

        // Assert
        AssertOneDistinctInstancePerStart(results.ToList());
        Assert.IsEmpty(gate.RegistrationCalls, $"Starts called a registration grain: {string.Join(", ", gate.RegistrationCalls)}");
    }

    private async Task Deploy(WorkflowDefinition workflow)
    {
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId);
        await processGrain.DeployVersion(workflow, "<placeholder/>");
    }

    private static async Task<(List<Guid>[] Results, StartGate Gate)> StartConcurrentlyThroughGate(
        int starts, Func<Task<List<Guid>>> start)
    {
        var gateId = Guid.NewGuid().ToString("N");
        var gate = new StartGate(starts);
        Gates[gateId] = gate;
        RequestContext.Set(GateKey, gateId);
        try
        {
            var calls = Enumerable.Range(0, starts).Select(_ => start()).ToList();
            return (await Task.WhenAll(calls).WaitAsync(GateTimeout), gate);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"{starts} concurrent starts did not all reach their new instance within {GateTimeout}: " +
                "they were serialized.");
            throw;
        }
        finally
        {
            RequestContext.Remove(GateKey);
            gate.Open();
            Gates.TryRemove(gateId, out _);
        }
    }

    private static void AssertOneDistinctInstancePerStart(List<List<Guid>> perStart)
    {
        Assert.IsTrue(perStart.All(ids => ids.Count == 1),
            $"Each start should create exactly one instance; got [{string.Join(", ", perStart.Select(ids => ids.Count))}].");
        var all = perStart.SelectMany(ids => ids).ToList();
        Assert.HasCount(all.Count, all.Distinct().ToList());
    }

    /// <summary>Opens once <c>expected</c> distinct instances have received a gated call.</summary>
    private sealed class StartGate(int expected)
    {
        private readonly HashSet<GrainId> _arrived = [];
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> RegistrationCalls { get; } = new();

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
            if (RequestContext.Get(GateKey) is string gateId && Gates.TryGetValue(gateId, out var gate))
            {
                if (context.Grain is IMessageStartEventListenerGrain or ISignalStartEventListenerGrain
                    or IConditionalStartEventListenerGrain or IConditionalStartEventRegistryGrain)
                {
                    gate.RegistrationCalls.Enqueue($"{context.InterfaceName}.{context.MethodName}");
                }

                if (context.Grain is IWorkflowInstanceGrain)
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
