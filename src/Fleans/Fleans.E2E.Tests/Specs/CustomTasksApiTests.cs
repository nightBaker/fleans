using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/70-api-read-endpoints-negative-paths/ — Custom-task catalog section (manual plan retired — automated here)
// The engine bundles no plugins, so the catalog is only non-empty in the split-role topology,
// where AspireFixture attaches Fleans.E2E.PluginHost and its `e2e-probe` plugin registers
// itself on silo startup. CI: dotnet.yml `e2e-split-roles` job.
[TestClass]
[TestCategory("E2E")]
[TestCategory(E2ECategories.SplitRoles)]
public class CustomTasksApiTests : WorkflowE2ETestBase
{
    private const string Probe = "e2e-probe";

    [TestInitialize]
    public void RequireSplitTopology()
    {
        if (!AspireFixture.IsSplitRoles)
        {
            Assert.Inconclusive(
                $"Needs the split-role topology — run with {AspireFixture.SplitRolesEnvVar}=true " +
                $"(filter TestCategory={E2ECategories.SplitRoles}).");
        }
    }

    [TestMethod]
    public async Task ListCustomTasks_ContainsProbeWithSchema()
    {
        // Catalog registration happens during silo startup; poll briefly rather than read once.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var entries = await ApiClient.GetCustomTasksAsync();
        while (entries.All(e => e.TaskType != Probe) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            entries = await ApiClient.GetCustomTasksAsync();
        }

        var probe = entries.SingleOrDefault(e => e.TaskType == Probe);
        Assert.IsNotNull(probe, $"'{Probe}' missing from catalog: [{string.Join(",", entries.Select(e => e.TaskType))}].");
        Assert.AreEqual("E2E Probe", probe.DisplayName);
        Assert.IsNotEmpty(probe.SiloNames, "At least one silo must host the plugin.");
        Assert.AreEqual(probe.SiloNames.Count, probe.SiloNames.Distinct().Count(), "Silo names must be unique.");

        Assert.IsNotNull(probe.ParameterSchema);
        Assert.IsEmpty(probe.ParameterSchema.Parameters);

        // Task types are unique in the catalog.
        Assert.AreEqual(entries.Count, entries.Select(e => e.TaskType).Distinct().Count());
    }

    [TestMethod]
    public async Task GetCustomTask_KnownType_MatchesListEntry_UnknownType_Returns404()
    {
        var single = await ApiClient.GetCustomTaskAsync(Probe);
        Assert.IsNotNull(single);
        Assert.AreEqual(Probe, single.TaskType);
        Assert.AreEqual("E2E Probe", single.DisplayName);

        var fromList = (await ApiClient.GetCustomTasksAsync()).Single(e => e.TaskType == Probe);
        CollectionAssert.AreEquivalent(fromList.SiloNames.ToList(), single.SiloNames.ToList());
        CollectionAssert.AreEqual(
            fromList.ParameterSchema!.Parameters.Select(p => p.Name).ToList(),
            single.ParameterSchema!.Parameters.Select(p => p.Name).ToList());

        Assert.IsNull(await ApiClient.GetCustomTaskAsync("no-such-task-type-766"),
            "Unknown task type must return 404.");
    }
}
