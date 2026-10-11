using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Admin UI smoke: /admin/custom-tasks (CustomTaskCatalog.razor) — issue #772.
// The E2E stack runs the Api in the Combined role with the RestCaller plugin registered
// (Fleans.Api/Program.cs), so the catalog is never empty.
[TestClass]
[TestCategory("E2E")]
public class CustomTaskCatalogPageTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task Catalog_RendersEveryRegisteredPlugin_MatchingApi()
    {
        var entries = await ApiClient.GetCustomTasksAsync();
        Assert.IsNotEmpty(entries, "Expected at least the built-in RestCaller plugin to be registered.");

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
