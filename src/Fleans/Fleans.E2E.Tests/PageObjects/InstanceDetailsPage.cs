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

    /// <summary>A rendered diagram element by BPMN id (bpmn-js tags each shape with <c>data-element-id</c>).</summary>
    public ILocator DiagramElement(string elementId) =>
        BpmnCanvas.Locator($".djs-element[data-element-id='{elementId}']");

    /// <summary>Journal panel entries, newest first.</summary>
    public ILocator JournalEntries => _page.GetByTestId("instance-journal-entry");

    /// <summary>Sequence flows marked as taken by the run (on the diagram's own flows).</summary>
    public ILocator TakenFlows => BpmnCanvas.Locator(".djs-element.bpmn-flow-taken");

    public ILocator Journal => _page.GetByTestId("instance-journal");

    public ILocator VariablesPanel => _page.GetByTestId("instance-variables");

    public ILocator PropertiesPanel => _page.Locator("#instance-properties-panel");

    /// <summary>Expands the journal entry whose text contains <paramref name="text"/> and returns its details.</summary>
    public async Task<ILocator> ExpandJournalEntryAsync(string text)
    {
        var entry = JournalEntries.Filter(new() { HasTextString = text });
        await entry.GetByTestId("instance-journal-entry-toggle").ClickAsync();
        return entry.GetByTestId("instance-journal-details");
    }

    /// <summary>The replay bar's "live" / "entry k of n" read-out.</summary>
    public ILocator ReplayPosition => _page.GetByTestId("instance-replay-position");

    public Task ReplayToStartAsync() => _page.GetByTestId("instance-replay-start").ClickAsync();

    public Task ReplayStepForwardAsync() => _page.GetByTestId("instance-replay-forward").ClickAsync();

    public Task ReplayToLiveAsync() => _page.GetByTestId("instance-replay-live").ClickAsync();

    public Task CollapseJournalAsync() => _page.GetByTestId("instance-journal-collapse").ClickAsync();

    public Task ExpandJournalAsync() => _page.GetByTestId("instance-journal-expand").ClickAsync();

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
