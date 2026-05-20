using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.PageObjects;

/// <summary>
/// Wraps Fleans.Web's <c>/process-instance/{guid}</c> Razor page (<c>ProcessInstance.razor</c>).
/// </summary>
public sealed class InstanceDetailsPage
{
    private readonly IPage _page;

    public InstanceDetailsPage(IPage page)
    {
        _page = page;
    }

    public async Task OpenAsync(Guid workflowInstanceId)
    {
        await _page.GotoAsync($"/process-instance/{workflowInstanceId:D}");
    }

    public ILocator BpmnCanvas => _page.Locator("#bpmn-canvas");

    /// <summary>
    /// Asserts the page loaded for the given instance and shows at least one
    /// "Completed" badge. ProcessInstance.razor renders one status badge in the
    /// PageHeader plus one per activity row in the activities table, so this
    /// matches any of them — the API-side IsCompleted check in the spec is the
    /// authoritative state assertion.
    /// </summary>
    public async Task AssertCompletedAsync(Guid workflowInstanceId)
    {
        await Expect(_page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex($"/process-instance/{workflowInstanceId:D}$"));

        await Expect(_page.Locator("fluent-badge", new() { HasTextString = "Completed" }).First)
            .ToBeVisibleAsync(new() { Timeout = 15_000 });
    }
}
