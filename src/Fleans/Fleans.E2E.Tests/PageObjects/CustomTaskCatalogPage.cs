using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.PageObjects;

/// <summary>
/// Wraps Fleans.Web's <c>/admin/custom-tasks</c> page (<c>CustomTaskCatalog.razor</c>): one grid
/// row per registered custom-task plugin (Task Type, Display Name, Silos, Parameters count).
/// </summary>
public sealed class CustomTaskCatalogPage
{
    private readonly IPage _page;

    public CustomTaskCatalogPage(IPage page)
    {
        _page = page;
    }

    public ILocator Grid => _page.GetByTestId("custom-task-catalog-grid");

    public async Task OpenAsync()
    {
        await BlazorPage.GotoInteractiveAsync(_page, "/admin/custom-tasks");
        await Expect(Grid).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    /// <summary>The row whose first cell (Task Type) is exactly <paramref name="taskType"/>.</summary>
    public ILocator Row(string taskType) =>
        Grid.Locator("tbody tr").Filter(new()
        {
            Has = _page.Locator("td:first-child", new() { HasTextRegex = new System.Text.RegularExpressions.Regex($"^\\s*{System.Text.RegularExpressions.Regex.Escape(taskType)}\\s*$") }),
        });

    public ILocator Cells(string taskType) => Row(taskType).Locator("td");
}
