using System.Net;
using Fleans.Worker.Placement;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Fleans.Application.Tests.Placement;

/// <summary>
/// Plugin-host isolation contract — proves that <see cref="WorkerPlacementDirector"/>
/// excludes silos whose <c>SiloName</c> starts with <c>plugin-</c> from
/// <c>[WorkerPlacement]</c> placement decisions.
///
/// <para>
/// Path C of the task plan: a focused unit-level test against
/// <see cref="WorkerPlacementDirector.OnAddActivation"/> with a faked
/// <see cref="IPlacementContext"/>, <see cref="IGrainFactory"/>, and
/// <see cref="IManagementGrain"/>. A full <c>TestCluster</c> spin-up (Path A) was
/// considered but rejected because the engine's <c>ScriptExecutorGrain</c> also
/// carries <c>[StatelessWorker]</c>, which short-circuits placement to the local
/// silo and would not exercise <c>WorkerPlacementDirector</c> reliably inside an
/// in-process two-silo cluster. Path C exercises the same code path
/// (<see cref="WorkerPlacementDirector.OnAddActivation"/>) with a controlled
/// membership list, which is the actual contract the plugin-host-isolation
/// initiative promises.
/// </para>
///
/// <para>
/// What this guarantees: given a cluster membership list containing
/// <c>worker-*</c>, <c>plugin-*</c>, <c>combined-*</c>, and <c>core-*</c> silos
/// that are ALL reported as compatible by
/// <see cref="IPlacementContext.GetCompatibleSilos"/>, the director's returned
/// <see cref="SiloAddress"/> always belongs to a <c>worker-*</c> or
/// <c>combined-*</c> silo and never to a <c>plugin-*</c> or <c>core-*</c> silo.
/// Round-robin is exercised across many calls so a single lucky pick cannot
/// hide a bug.
/// </para>
/// </summary>
[TestClass]
public class PluginHostIsolationIntegrationTests
{
    private sealed record Scenario(
        WorkerPlacementDirector Director,
        IPlacementContext Context,
        SiloAddress Worker,
        SiloAddress Plugin,
        SiloAddress Combined,
        SiloAddress Core);

    private static SiloAddress NewSilo(int port) =>
        SiloAddress.New(IPAddress.Loopback, port, generation: 0);

    private static MembershipEntry Host(SiloAddress address, string siloName) =>
        new()
        {
            SiloAddress = address,
            SiloName = siloName,
            Status = SiloStatus.Active,
            HostName = "test-host",
        };

    private static PlacementTarget NewTarget()
    {
        var grainId = GrainId.Create("test-grain-type", "test-grain-key");
        return new PlacementTarget(
            grainId,
            requestContextData: new Dictionary<string, object>(),
            interfaceType: default,
            interfaceVersion: 0);
    }

    /// <summary>
    /// Build a four-silo membership (worker / plugin / combined / core) and wire up
    /// fakes so <see cref="WorkerPlacementDirector"/> can run in isolation.
    /// All four silos are reported as compatible by <see cref="IPlacementContext"/>
    /// — the dangerous case the director must defend against.
    /// </summary>
    private static Scenario BuildScenario()
    {
        var worker = NewSilo(11111);
        var plugin = NewSilo(22222);
        var combined = NewSilo(33333);
        var core = NewSilo(44444);

        var hosts = new[]
        {
            Host(worker, "worker-machine1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            Host(plugin, "plugin-machine2-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            Host(combined, "combined-machine3-cccccccccccccccccccccccccccccccc"),
            Host(core, "core-machine4-dddddddddddddddddddddddddddddddd"),
        };

        var management = Substitute.For<IManagementGrain>();
        management.GetDetailedHosts(onlyActive: true).Returns(Task.FromResult(hosts));

        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IManagementGrain>(0, null).Returns(management);

        var context = Substitute.For<IPlacementContext>();
        context.GetCompatibleSilos(Arg.Any<PlacementTarget>())
            .Returns(new[] { worker, plugin, combined, core });

        var director = new WorkerPlacementDirector(grainFactory, NullLogger<WorkerPlacementDirector>.Instance);
        return new Scenario(director, context, worker, plugin, combined, core);
    }

    [TestMethod]
    public async Task OnAddActivation_NeverPlacesOnPluginSilo_WhenWorkerAndCombinedAreAvailable()
    {
        var s = BuildScenario();
        var target = NewTarget();
        var eligible = new HashSet<SiloAddress> { s.Worker, s.Combined };

        // Hammer the director with enough calls to exhaust both round-robin slots
        // multiple times — proves the filter is structural, not lucky.
        for (var i = 0; i < 64; i++)
        {
            var pick = await s.Director.OnAddActivation(WorkerPlacementStrategy.Singleton, target, s.Context);
            Assert.AreNotEqual(s.Plugin, pick,
                $"Iteration {i}: WorkerPlacementDirector returned the plugin- silo address. " +
                "[WorkerPlacement] grains (Script, Condition) must never land on a plugin- silo.");
            Assert.IsTrue(eligible.Contains(pick),
                $"Iteration {i}: returned silo {pick} is neither the worker- nor combined- silo.");
        }
    }

    [TestMethod]
    public async Task OnAddActivation_IgnoresCoreSilo_EvenWhenCompatible()
    {
        // Same scenario as above — also confirms core- silos are excluded by the
        // role filter (the director treats core- as "not a worker role").
        var s = BuildScenario();
        var target = NewTarget();

        for (var i = 0; i < 32; i++)
        {
            var pick = await s.Director.OnAddActivation(WorkerPlacementStrategy.Singleton, target, s.Context);
            Assert.AreNotEqual(s.Core, pick, $"Iteration {i}: returned the core- silo.");
            Assert.AreNotEqual(s.Plugin, pick, $"Iteration {i}: returned the plugin- silo.");
            Assert.IsTrue(pick.Equals(s.Worker) || pick.Equals(s.Combined),
                $"Iteration {i}: pick {pick} is not worker or combined.");
        }
    }

    [TestMethod]
    public async Task OnAddActivation_RoundRobinsBetweenWorkerAndCombined()
    {
        // Belt-and-suspenders: prove both eligible silos are actually exercised.
        // A "return first silo always" bug would still pass the negative
        // assertions above — this assertion catches it.
        var s = BuildScenario();
        var target = NewTarget();

        var picks = new HashSet<SiloAddress>();
        for (var i = 0; i < 16; i++)
        {
            picks.Add(await s.Director.OnAddActivation(WorkerPlacementStrategy.Singleton, target, s.Context));
        }

        Assert.IsTrue(picks.Contains(s.Worker), "Round-robin never selected the worker- silo.");
        Assert.IsTrue(picks.Contains(s.Combined), "Round-robin never selected the combined- silo.");
        Assert.AreEqual(2, picks.Count, $"Unexpected silos were selected: {string.Join(", ", picks)}");
    }

    [TestMethod]
    public async Task OnAddActivation_FallsBackToAnyCompatibleSilo_WhenNoWorkerRoleAvailable()
    {
        // Documents the fallback behavior: if a cluster has ONLY plugin-/core-
        // silos compatible with a [WorkerPlacement] grain, the director logs a
        // warning and accepts any compatible silo rather than throwing. The
        // plugin-host-isolation initiative does NOT change this fallback — it
        // only tightens the primary candidate filter. This test guards against
        // accidental regressions where someone tightens the fallback and breaks
        // single-silo dev clusters.
        var plugin = NewSilo(22222);
        var hosts = new[]
        {
            Host(plugin, "plugin-only-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
        };
        var management = Substitute.For<IManagementGrain>();
        management.GetDetailedHosts(onlyActive: true).Returns(Task.FromResult(hosts));

        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IManagementGrain>(0, null).Returns(management);

        var context = Substitute.For<IPlacementContext>();
        context.GetCompatibleSilos(Arg.Any<PlacementTarget>()).Returns(new[] { plugin });

        var director = new WorkerPlacementDirector(grainFactory, NullLogger<WorkerPlacementDirector>.Instance);
        var pick = await director.OnAddActivation(WorkerPlacementStrategy.Singleton, NewTarget(), context);

        Assert.AreEqual(plugin, pick,
            "Fallback must return the only compatible silo when no worker-/combined- candidates exist.");
    }
}
