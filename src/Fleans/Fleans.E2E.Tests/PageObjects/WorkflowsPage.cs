using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.PageObjects;

/// <summary>
/// Wraps Fleans.Web's <c>/workflows</c> page (<c>Workflows.razor</c>): the process-definition
/// list grouped by key (latest version as the parent row, older versions as collapsed children),
/// with per-row Start / Instances / Disable|Enable actions and a debounced key search.
/// </summary>
public sealed class WorkflowsPage
{
    private readonly IPage _page;

    public WorkflowsPage(IPage page)
    {
        _page = page;
    }

    public ILocator Grid => _page.GetByTestId("workflows-grid");

    public ILocator SearchInput => _page.GetByTestId("workflows-search").Locator("input");

    public ILocator SuccessMessage => _page.GetByTestId("workflows-action-success");

    public async Task OpenAsync()
    {
        await BlazorPage.GotoInteractiveAsync(_page, "/workflows");
        await Expect(Grid).ToBeVisibleAsync();
    }

    /// <summary>
    /// Types into the search box (bound on <c>change</c>, debounced 300 ms server-side) and waits
    /// until the grid shows only rows for <paramref name="processKey"/>.
    /// </summary>
    public async Task SearchAsync(string processKey)
    {
        await SearchInput.FillAsync(processKey);
        await SearchInput.PressAsync("Enter");
        await Expect(Row(processKey)).ToBeVisibleAsync(new() { Timeout = 15_000 });
        // Wait for the debounced re-query to land (every row belongs to the key) so later
        // clicks don't race a grid re-render that replaces the row's buttons.
        await Expect(Grid.Locator("tbody tr").Filter(new()
            {
                HasNot = _page.Locator("strong", new() { HasTextString = processKey }),
            }))
            .ToHaveCountAsync(0, new() { Timeout = 15_000 });
    }

    /// <summary>All rendered rows for <paramref name="processKey"/> (parent + any expanded children).</summary>
    public ILocator AllRows(string processKey) =>
        Grid.Locator("tbody tr")
            .Filter(new() { Has = _page.Locator("strong", new() { HasTextString = processKey }) });

    /// <summary>
    /// The group (parent) row for <paramref name="processKey"/> — the latest version. Children
    /// render directly after their parent, so the first matching row is always the parent.
    /// </summary>
    public ILocator Row(string processKey) => AllRows(processKey).First;

    /// <summary>The row for a specific version of <paramref name="processKey"/>.</summary>
    public ILocator VersionRow(string processKey, int version) =>
        AllRows(processKey).Filter(new() { Has = _page.Locator("fluent-badge", new() { HasTextString = $"v{version}" }) });

    /// <summary>Clicks the hierarchical expand/collapse toggle on the group row.</summary>
    public Task ToggleVersionsAsync(string processKey) =>
        Row(processKey).Locator("fluent-button.hierarchical-expand-button").ClickAsync();

    public ILocator VersionBadge(string processKey) =>
        Row(processKey).Locator("fluent-badge", new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^v\d+$") });

    public ILocator DisabledBadge(string processKey) =>
        Row(processKey).Locator("fluent-badge", new() { HasTextString = "Disabled" });

    public ILocator ActionButton(string processKey, string label) =>
        Row(processKey).Locator("fluent-button", new() { HasTextString = label });

    public Task ClickActionAsync(string processKey, string label) =>
        ActionButton(processKey, label).ClickAsync();
}
