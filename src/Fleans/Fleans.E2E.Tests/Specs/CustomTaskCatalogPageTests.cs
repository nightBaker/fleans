using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Admin UI smoke: /admin/custom-tasks (CustomTaskCatalog.razor) — issue #772.
[TestClass]
[TestCategory("E2E")]
public class CustomTaskCatalogPageTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task Catalog_RendersEveryRegisteredPlugin_MatchingApi()
    {
        // The worker registers once connected, possibly after the fixture is up;
        // poll briefly rather than read once.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var entries = await ApiClient.GetCustomTasksAsync();
        while (entries.All(e => e.TaskType != "e2e-probe") && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            entries = await ApiClient.GetCustomTasksAsync();
        }
        Assert.IsTrue(entries.Any(e => e.TaskType == "e2e-probe"),
            $"Expected the e2e-probe task type to be registered: [{string.Join(",", entries.Select(e => e.TaskType))}].");

        var page = new CustomTaskCatalogPage(Page);
        await page.OpenAsync();

        // Private entries also hold an edit button: CustomTaskAccessTests covers them.
        foreach (var entry in entries.Where(e => e.Visibility == Fleans.Application.CustomTasks.CustomTaskVisibility.Public))
        {
            var row = page.Row(entry.TaskType);
            await Expect(row).ToHaveCountAsync(1);
            await Expect(page.Cells(entry.TaskType)).ToHaveTextAsync([
                entry.TaskType,
                entry.DisplayName ?? "—",
                entry.Category ?? "—",
                "Public",
                "—",
                entry.SiloNames.Count == 0 ? "No silos" : string.Join(", ", entry.SiloNames),
                (entry.ParameterSchema?.Parameters.Count ?? 0).ToString(),
            ]);
        }
    }
}
