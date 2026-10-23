using System.Collections.Immutable;
using System.Net;
using Fleans.Worker.Placement;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Fleans.Application.Tests.Placement;

/// <summary>
/// Plugin-host isolation contract — proves that <see cref="WorkerPlacementFilterDirector"/>
/// removes silos whose <c>SiloName</c> does not start with <c>worker-</c> / <c>combined-</c>
/// (so <c>plugin-</c> and <c>core-</c> silos) from the candidates of a
/// <c>[WorkerPlacement]</c> grain.
///
/// <para>
/// A focused unit test against <see cref="WorkerPlacementFilterDirector.Filter"/> with a faked
/// <see cref="IClusterMembershipService"/>: every silo is passed in as compatible — the
/// dangerous case the filter must defend against. That the engine grains actually carry the
/// filter (alongside <c>[StatelessWorker]</c>) is proven by
/// <see cref="WorkerGrainPlacementResolutionTests"/>.
/// </para>
/// </summary>
[TestClass]
public class PluginHostIsolationIntegrationTests
{
    private static readonly SiloAddress Worker = NewSilo(11111);
    private static readonly SiloAddress Plugin = NewSilo(22222);
    private static readonly SiloAddress Combined = NewSilo(33333);
    private static readonly SiloAddress Core = NewSilo(44444);

    private static SiloAddress NewSilo(int port) =>
        SiloAddress.New(IPAddress.Loopback, port, generation: 0);

    private static PlacementTarget NewTarget() =>
        new(GrainId.Create("test-grain-type", "test-grain-key"),
            requestContextData: new Dictionary<string, object>(),
            interfaceType: default,
            interfaceVersion: 0);

    private static WorkerPlacementFilterDirector NewDirector(params (SiloAddress Address, string Name)[] members)
    {
        var snapshot = new ClusterMembershipSnapshot(
            members.ToImmutableDictionary(
                m => m.Address,
                m => new ClusterMember(m.Address, SiloStatus.Active, m.Name)),
            new MembershipVersion(1));

        var membership = Substitute.For<IClusterMembershipService>();
        membership.CurrentSnapshot.Returns(snapshot);

        return new WorkerPlacementFilterDirector(membership, NullLogger<WorkerPlacementFilterDirector>.Instance);
    }

    private static WorkerPlacementFilterDirector FourRoleDirector() => NewDirector(
        (Worker, "worker-machine1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        (Plugin, "plugin-machine2-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        (Combined, "combined-machine3-cccccccccccccccccccccccccccccccc"),
        (Core, "core-machine4-dddddddddddddddddddddddddddddddd"));

    [TestMethod]
    public void Filter_KeepsOnlyWorkerAndCombinedSilos()
    {
        var director = FourRoleDirector();

        var result = director.Filter(
            new WorkerPlacementFilterStrategy(), NewTarget(), new[] { Worker, Plugin, Combined, Core }).ToArray();

        CollectionAssert.AreEquivalent(new[] { Worker, Combined }, result,
            "[WorkerPlacement] grains (Script, Condition) must only be placed on worker-/combined- silos, " +
            "never on plugin- or core- silos.");
    }

    [TestMethod]
    public void Filter_DoesNotAddSilosThatWereNotCompatible()
    {
        // Orleans passes in only silos that host the grain type; the filter must narrow, not widen.
        var director = FourRoleDirector();

        var result = director.Filter(
            new WorkerPlacementFilterStrategy(), NewTarget(), new[] { Combined, Core }).ToArray();

        CollectionAssert.AreEquivalent(new[] { Combined }, result);
    }

    [TestMethod]
    public void Filter_FallsBackToAllCompatibleSilos_WhenNoWorkerRoleAvailable()
    {
        // Documents the fallback: if only plugin-/core- (or unnamed test) silos are compatible,
        // the filter logs a warning and returns them all rather than leaving nothing to place on.
        // Guards against tightening the fallback and breaking single-silo dev/test clusters.
        var director = NewDirector((Plugin, "plugin-only-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"));

        var result = director.Filter(
            new WorkerPlacementFilterStrategy(), NewTarget(), new[] { Plugin }).ToArray();

        CollectionAssert.AreEqual(new[] { Plugin }, result,
            "Fallback must return the compatible silos when no worker-/combined- candidates exist.");
    }

    [TestMethod]
    public void Filter_TreatsSiloMissingFromMembershipAsNonWorker()
    {
        var director = NewDirector((Worker, "worker-machine1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        var result = director.Filter(
            new WorkerPlacementFilterStrategy(), NewTarget(), new[] { Worker, Core }).ToArray();

        CollectionAssert.AreEqual(new[] { Worker }, result);
    }
}
