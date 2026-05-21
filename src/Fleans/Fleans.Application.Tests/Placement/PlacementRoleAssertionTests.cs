using Fleans.Application.CustomTasks;
using Fleans.Application.Placement;
using Fleans.Worker.Conditions;
using Fleans.Worker.Placement;
using Fleans.Worker.Scripts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;

namespace Fleans.Application.Tests.Placement;

/// <summary>
/// Regression tests for #457 — `PlacementRoleAssertion` fails fast on Fleans:Role /
/// placement-attribute mismatch. Uses the testable <c>AssertPlacements(...)</c> seam
/// to validate the 7-case role × attribute matrix without booting a TestCluster.
/// </summary>
[TestClass]
public class PlacementRoleAssertionTests
{
    private static readonly (Type, PlacementRoleAssertion.PlacementKind)[] _workerGrain =
    [
        (typeof(ScriptExecutorGrain), PlacementRoleAssertion.PlacementKind.Worker)
    ];

    private static readonly (Type, PlacementRoleAssertion.PlacementKind)[] _coreGrain =
    [
        (typeof(Fleans.Application.CustomTasks.CustomTaskCatalogGrain), PlacementRoleAssertion.PlacementKind.Core)
    ];

    private static readonly (Type, PlacementRoleAssertion.PlacementKind)[] _bothGrains =
    [
        (typeof(ScriptExecutorGrain), PlacementRoleAssertion.PlacementKind.Worker),
        (typeof(Fleans.Application.CustomTasks.CustomTaskCatalogGrain), PlacementRoleAssertion.PlacementKind.Core),
    ];

    // Case 1: Core silo + [CorePlacement] grain → no throw
    [TestMethod]
    public void CoreSilo_CoreGrain_NoThrow()
        => Assert.AreEqual(1, PlacementRoleAssertion.AssertPlacements("core-host-x", "Core", _coreGrain));

    // Case 2: Core silo + [WorkerPlacement] grain → throws AC2 message
    [TestMethod]
    public void CoreSilo_WorkerGrain_ThrowsWithAC2Message()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => PlacementRoleAssertion.AssertPlacements("core-host-x", "Core", _workerGrain));

        StringAssert.Contains(ex.Message, "[WorkerPlacement]");
        StringAssert.Contains(ex.Message, "Fleans:Role is 'Core'");
        StringAssert.Contains(ex.Message, "'Worker' or");
        StringAssert.Contains(ex.Message, "'Combined'");
        StringAssert.Contains(ex.Message, "Fleans:Role=Worker");
    }

    // Case 3: Worker silo + [WorkerPlacement] grain → no throw
    [TestMethod]
    public void WorkerSilo_WorkerGrain_NoThrow()
        => Assert.AreEqual(1, PlacementRoleAssertion.AssertPlacements("worker-host-x", "Worker", _workerGrain));

    // Case 4: Worker silo + [CorePlacement] grain → throws symmetric AC2
    [TestMethod]
    public void WorkerSilo_CoreGrain_ThrowsWithSymmetricMessage()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => PlacementRoleAssertion.AssertPlacements("worker-host-x", "Worker", _coreGrain));

        StringAssert.Contains(ex.Message, "[CorePlacement]");
        StringAssert.Contains(ex.Message, "Fleans:Role is 'Worker'");
        StringAssert.Contains(ex.Message, "'Core' or");
        StringAssert.Contains(ex.Message, "'Combined'");
        StringAssert.Contains(ex.Message, "Fleans:Role=Core");
    }

    // Case 5: Combined silo + both grain attributes → no throw (dev-mode 3-process topology)
    [TestMethod]
    public void CombinedSilo_BothGrains_NoThrow()
        => Assert.AreEqual(2, PlacementRoleAssertion.AssertPlacements("combined-host-x", "Combined", _bothGrains));

    // Case 6: Plugin silo + [WorkerPlacement] grain → throws (engine workers stay off plugin hosts)
    [TestMethod]
    public void PluginSilo_WorkerGrain_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => PlacementRoleAssertion.AssertPlacements("plugin-host-x", "Plugin", _workerGrain));
    }

    // Case 7: Plugin silo + [CorePlacement] grain → throws (engine core grains never on plugin hosts)
    [TestMethod]
    public void PluginSilo_CoreGrain_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => PlacementRoleAssertion.AssertPlacements("plugin-host-x", "Plugin", _coreGrain));
    }

    // Bonus: no grains → no throw (idempotent on hosts with no attributed grains)
    [TestMethod]
    public void NoGrains_NoThrow()
        => Assert.AreEqual(0, PlacementRoleAssertion.AssertPlacements(
            "core-host-x", "Core", Array.Empty<(Type, PlacementRoleAssertion.PlacementKind)>()));

    // Discovery: scanning own assemblies must surface the two engine [WorkerPlacement] grains
    [TestMethod]
    public void DiscoverPlacementGrains_FindsEngineWorkerGrains()
    {
        var asms = new[]
        {
            typeof(ScriptExecutorGrain).Assembly,
            typeof(ConditionExpressionEvaluatorGrain).Assembly,
        };
        var found = PlacementRoleAssertion.DiscoverPlacementGrains(asms).ToList();

        Assert.IsTrue(found.Any(g => g.Type == typeof(ScriptExecutorGrain) && g.Kind == PlacementRoleAssertion.PlacementKind.Worker));
        Assert.IsTrue(found.Any(g => g.Type == typeof(ConditionExpressionEvaluatorGrain) && g.Kind == PlacementRoleAssertion.PlacementKind.Worker));
    }
}
