using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.PageObjects;

/// <summary>
/// Wraps Fleans.Web's <c>/events</c> page (<c>Events.razor</c>): five read-only sections
/// projecting registered start events and active message/signal subscriptions. The page
/// is a point-in-time snapshot — it only re-queries on Refresh.
/// </summary>
public sealed class EventsPage
{
    private readonly IPage _page;

    public EventsPage(IPage page)
    {
        _page = page;
    }

    public ILocator Header => _page.GetByText("Registered Events", new() { Exact = true });

    public ILocator RefreshButton => _page.GetByTestId("events-refresh");

    public ILocator MessageStartGrid => _page.GetByTestId("events-message-start-grid");
    public ILocator SignalStartGrid => _page.GetByTestId("events-signal-start-grid");
    public ILocator ConditionalStartGrid => _page.GetByTestId("events-conditional-start-grid");
    public ILocator MessageSubscriptionsGrid => _page.GetByTestId("events-message-subscriptions-grid");
    public ILocator SignalSubscriptionsGrid => _page.GetByTestId("events-signal-subscriptions-grid");

    public async Task OpenAsync()
    {
        await BlazorPage.GotoInteractiveAsync(_page, "/events");
        await Expect(Header).ToBeVisibleAsync();
        await Expect(RefreshButton).ToBeVisibleAsync();
    }

    public async Task RefreshAsync()
    {
        await RefreshButton.ClickAsync();
    }

    public ILocator MessageStartRow(string messageName) => BlazorPage.GridRows(MessageStartGrid, messageName);
    public ILocator SignalStartRow(string signalName) => BlazorPage.GridRows(SignalStartGrid, signalName);
    public ILocator ConditionalStartRow(string processKey) => BlazorPage.GridRows(ConditionalStartGrid, processKey);
    public ILocator MessageSubscriptionRow(string messageName) => BlazorPage.GridRows(MessageSubscriptionsGrid, messageName);
    public ILocator SignalSubscriptionRow(string signalName) => BlazorPage.GridRows(SignalSubscriptionsGrid, signalName);

    /// <summary>Clicks Refresh until <paramref name="rows"/> resolves to <paramref name="expectedCount"/> rows.</summary>
    public Task RefreshUntilCountAsync(ILocator rows, int expectedCount) =>
        BlazorPage.RefreshUntilCountAsync(RefreshAsync, rows, expectedCount);

    /// <summary>
    /// The truncated GUID cell for <paramref name="id"/> (8 chars + "…"); the full value is in
    /// the FluentTooltip anchored to the span's id.
    /// </summary>
    public ILocator TruncatedId(ILocator row, Guid id) =>
        row.Locator($"span[id$='-{id:D}']");
}
