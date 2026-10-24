using System.Net;
using System.Reflection;
using Fleans.Application.Grains;
using Fleans.Application.Placement;
using Fleans.Application.Projections;
using Fleans.Worker.Placement;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Fleans.Application.Tests.Placement;

/// <summary>
/// #861 — the read-model projector prefers <c>projector-</c> silos and falls back to the core
/// silos, and a projector silo hosts no other Fleans grain.
/// </summary>
[TestClass]
public class ProjectorPlacementTests
{
    private static readonly SiloAddress Projector1 = NewSilo(11111);
    private static readonly SiloAddress Projector2 = NewSilo(11112);
    private static readonly SiloAddress Core = NewSilo(22222);
    private static readonly SiloAddress Combined = NewSilo(33333);
    private static readonly SiloAddress Worker = NewSilo(44444);
    private static readonly SiloAddress Plugin = NewSilo(55555);

    private static SiloAddress NewSilo(int port) => SiloAddress.New(IPAddress.Loopback, port, generation: 0);

    private static readonly (SiloAddress, string?)[] AllRoles =
    [
        (Projector1, "projector-host1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        (Projector2, "projector-host2-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        (Core, "core-host3-cccccccccccccccccccccccccccccccc"),
        (Combined, "combined-host4-dddddddddddddddddddddddddddddddd"),
        (Worker, "worker-host5-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
        (Plugin, "plugin-host6-ffffffffffffffffffffffffffffffff"),
    ];

    [TestMethod]
    public void SelectCandidates_PrefersProjectorSilos()
    {
        // Act
        var result = ProjectorPlacementDirector.SelectCandidates(
            [Projector1, Projector2, Core, Combined, Worker, Plugin], AllRoles);

        // Assert
        CollectionAssert.AreEquivalent(new[] { Projector1, Projector2 }, result);
    }

    [TestMethod]
    public void SelectCandidates_WithoutProjectorSilo_FallsBackToCoreAndCombined()
    {
        // Act
        var result = ProjectorPlacementDirector.SelectCandidates(
            [Core, Combined, Worker, Plugin], AllRoles);

        // Assert
        CollectionAssert.AreEquivalent(new[] { Core, Combined }, result,
            "Without a fleans-projector deployment the projector stays on the core silos, as before.");
    }

    [TestMethod]
    public void SelectCandidates_IgnoresProjectorSiloThatIsNotCompatible()
    {
        // A projector silo of an older build that lacks the grain must not be picked.
        var result = ProjectorPlacementDirector.SelectCandidates([Core], AllRoles);

        CollectionAssert.AreEqual(new[] { Core }, result);
    }

    [TestMethod]
    public void SelectCandidates_IgnoresProjectorSiloThatIsNotActive()
    {
        // Membership lists active hosts only; a compatible silo missing from it is not a candidate.
        var result = ProjectorPlacementDirector.SelectCandidates(
            [Projector1, Core], [(Core, "core-host3-cccccccccccccccccccccccccccccccc")]);

        CollectionAssert.AreEqual(new[] { Core }, result);
    }

    [TestMethod]
    public void SelectCandidates_WithNeitherRole_IsEmpty()
    {
        var result = ProjectorPlacementDirector.SelectCandidates([Worker, Plugin], AllRoles);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task OnAddActivation_PlacesOnProjectorSilos_RoundRobin()
    {
        // Arrange
        var director = NewDirector(AllRoles);
        var context = NewContext(Projector1, Projector2, Core, Combined);

        // Act
        var picks = new List<SiloAddress>();
        for (var i = 0; i < 4; i++)
            picks.Add(await director.OnAddActivation(new ProjectorPlacementStrategy(), NewTarget(), context));

        // Assert
        CollectionAssert.AreEquivalent(new[] { Projector1, Projector2 }, picks.Distinct().ToArray());
    }

    [TestMethod]
    public async Task OnAddActivation_WithNoProjectorOrCoreSilo_FallsBackToAnyCompatibleSilo()
    {
        // Arrange: single unnamed test silo.
        var unnamed = NewSilo(6666);
        var director = NewDirector([(unnamed, "Primary")]);

        // Act
        var pick = await director.OnAddActivation(new ProjectorPlacementStrategy(), NewTarget(), NewContext(unnamed));

        // Assert
        Assert.AreEqual(unnamed, pick);
    }

    [TestMethod]
    public void ProjectorSilos_AreNotCoreOrWorkerSilos()
    {
        // Core grains (WorkflowInstance, ...) and worker grains (scripts, conditions) never land
        // on a projector silo.
        Assert.IsFalse(CorePlacementDirector.HasCoreRole("projector-host1-aaaa"));
        Assert.IsFalse(InvokeHasWorkerRole("projector-host1-aaaa"));
        Assert.IsTrue(ProjectorPlacementDirector.HasProjectorRole("projector-host1-aaaa"));
        Assert.IsFalse(ProjectorPlacementDirector.HasProjectorRole("core-host1-aaaa"));
        Assert.IsFalse(ProjectorPlacementDirector.HasProjectorRole("combined-host1-aaaa"));
        Assert.IsFalse(ProjectorPlacementDirector.HasProjectorRole(null));
    }

    [TestMethod]
    public void WorkflowInstanceProjector_CarriesProjectorPlacementOnly()
    {
        var placement = typeof(WorkflowInstanceProjector).GetCustomAttributes<PlacementAttribute>(inherit: false).ToArray();

        Assert.HasCount(1, placement);
        Assert.IsInstanceOfType<ProjectorPlacementAttribute>(placement[0]);
    }

    [TestMethod]
    public void KeepOnlyProjectorGrains_RemovesEveryOtherFleansGrain()
    {
        // Arrange
        var orleansGrain = typeof(Grain); // stands in for Orleans' own grains: not from a Fleans assembly
        var options = new GrainTypeOptions();
        options.Classes.Add(typeof(WorkflowInstance));
        options.Classes.Add(typeof(WorkflowInstanceProjector));
        options.Classes.Add(typeof(Fleans.Worker.Scripts.ScriptExecutorGrain));
        options.Classes.Add(orleansGrain);

        // Act
        ProjectorSilo.KeepOnlyProjectorGrains(options);

        // Assert
        CollectionAssert.AreEquivalent(new[] { typeof(WorkflowInstanceProjector), orleansGrain }, options.Classes.ToArray(),
            "A projector silo keeps the projector and Orleans' own grains, and no other Fleans grain.");
    }

    private static bool InvokeHasWorkerRole(string siloName) =>
        (bool)typeof(WorkerPlacementFilterDirector)
            .GetMethod("HasWorkerRole", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [siloName])!;

    private static ProjectorPlacementDirector NewDirector(IEnumerable<(SiloAddress Address, string? Name)> hosts)
    {
        var entries = hosts.Select(h => new MembershipEntry
        {
            SiloAddress = h.Address,
            SiloName = h.Name,
            Status = SiloStatus.Active,
        }).ToArray();

        var management = Substitute.For<IManagementGrain>();
        management.GetDetailedHosts(true).Returns(Task.FromResult(entries));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IManagementGrain>(0).Returns(management);

        return new ProjectorPlacementDirector(grainFactory, NullLogger<ProjectorPlacementDirector>.Instance);
    }

    private static IPlacementContext NewContext(params SiloAddress[] compatible)
    {
        var context = Substitute.For<IPlacementContext>();
        context.GetCompatibleSilos(Arg.Any<PlacementTarget>()).Returns(compatible);
        return context;
    }

    private static PlacementTarget NewTarget() =>
        new(GrainId.Create("projector", Guid.NewGuid().ToString("D")),
            requestContextData: new Dictionary<string, object>(),
            interfaceType: default,
            interfaceVersion: 0);
}
