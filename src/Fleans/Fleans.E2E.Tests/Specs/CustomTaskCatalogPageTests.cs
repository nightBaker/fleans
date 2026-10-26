using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Admin UI smoke: /admin/custom-tasks (CustomTaskCatalog.razor) — issue #772.
// The engine bundles no plugins, so the catalog is only non-empty in the split-role topology,
// where Fleans.E2E.PluginHost registers its `e2e-probe` plugin. CI: dotnet.yml `e2e-split-roles`.
[TestClass]
[TestCategory("E2E")]
[TestCategory(E2ECategories.SplitRoles)]
public class CustomTaskCatalogPageTests : WorkflowE2ETestBase
{
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
    public async Task Catalog_RendersEveryRegisteredPlugin_MatchingApi()
    {
        var entries = await ApiClient.GetCustomTasksAsync();
        Assert.IsNotEmpty(entries, "Expected at least the e2e-probe plugin to be registered.");

        var page = new CustomTaskCatalogPage(Page);
        await page.OpenAsync();

        foreach (var entry in entries)
        {
            var row = page.Row(entry.TaskType);
            await Expect(row).ToHaveCountAsync(1);
            await Expect(page.Cells(entry.TaskType)).ToHaveTextAsync([
                entry.TaskType,
                entry.DisplayName ?? "—",
                entry.SiloNames.Count == 0 ? "No silos" : string.Join(", ", entry.SiloNames),
                (entry.ParameterSchema?.Parameters.Count ?? 0).ToString(),
            ]);
        }
    }
}
