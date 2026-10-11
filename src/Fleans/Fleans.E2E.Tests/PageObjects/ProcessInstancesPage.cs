using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.PageObjects;

/// <summary>
/// Wraps Fleans.Web's <c>/process-instances/{key}[/{version}]</c> page (<c>ProcessInstances.razor</c>).
/// </summary>
public sealed class ProcessInstancesPage
{
    private readonly IPage _page;

    public ProcessInstancesPage(IPage page)
    {
        _page = page;
    }

    public ILocator Grid => _page.GetByTestId("process-instances-grid");

    /// <summary>Instance rows — each carries the full GUID in the Instance ID cell's <c>title</c>.</summary>
    public ILocator Rows => Grid.Locator("tbody tr").Filter(new() { Has = _page.Locator("span[title]") });

    public ILocator Title(string text) => _page.GetByText(text, new() { Exact = true });

    public async Task OpenAsync(string processDefinitionKey, int? version = null)
    {
        var key = Uri.EscapeDataString(processDefinitionKey);
        await BlazorPage.GotoInteractiveAsync(_page,
            version is null ? $"/process-instances/{key}" : $"/process-instances/{key}/{version.Value}");
    }

    public ILocator Row(Guid instanceId) =>
        Grid.Locator("tbody tr").Filter(new() { Has = _page.Locator($"span[title='{instanceId:D}']") });

    /// <summary>Asserts the instance row is rendered with the truncated id and the given status badge.</summary>
    public async Task AssertInstanceStatusAsync(Guid instanceId, string status)
    {
        var row = Row(instanceId);
        await Expect(row).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(row.Locator("fluent-badge")).ToHaveTextAsync(status);
        await Expect(row.Locator($"span[title='{instanceId:D}']"))
            .ToHaveTextAsync(instanceId.ToString("D")[..8] + "...");
    }

    public async Task ViewInstanceAsync(Guid instanceId)
    {
        await Row(instanceId).Locator("fluent-button", new() { HasTextString = "View" }).ClickAsync();
    }
}
