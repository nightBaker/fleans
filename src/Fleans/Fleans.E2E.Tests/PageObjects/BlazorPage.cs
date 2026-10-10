using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.PageObjects;

/// <summary>
/// Shared navigation / polling helpers for Fleans.Web's Blazor Server pages.
/// </summary>
internal static class BlazorPage
{
    /// <summary>
    /// Navigates to <paramref name="path"/> and waits until the SignalR circuit is
    /// interactive. Pages are prerendered, so markup is visible before event handlers are
    /// wired; a click before the circuit is up is silently dropped. MainLayout renders
    /// <c>[data-testid=blazor-interactive]</c> only when <c>RendererInfo.IsInteractive</c>.
    /// </summary>
    public static async Task GotoInteractiveAsync(IPage page, string path)
    {
        await page.GotoAsync(path);
        await WaitForInteractiveAsync(page);
    }

    public static async Task WaitForInteractiveAsync(IPage page)
    {
        await Expect(page.GetByTestId("blazor-interactive"))
            .ToBeAttachedAsync(new() { Timeout = 30_000 });
    }

    /// <summary>Body rows (<c>tbody tr</c>) of a FluentDataGrid that contain <paramref name="text"/>.</summary>
    public static ILocator GridRows(ILocator grid, string text) =>
        grid.Locator("tbody tr").Filter(new() { HasTextString = text });

    /// <summary>
    /// Repeats <paramref name="refresh"/> until <paramref name="locator"/> resolves to
    /// <paramref name="expectedCount"/> elements. For pages that are point-in-time snapshots
    /// (no push updates) observing projections written asynchronously by the engine.
    /// Each attempt uses Playwright's auto-waiting assertion with a short timeout, so there
    /// is no fixed sleep between attempts.
    /// </summary>
    public static async Task RefreshUntilCountAsync(
        Func<Task> refresh,
        ILocator locator,
        int expectedCount,
        int timeoutMs = 30_000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            await refresh();
            try
            {
                await Expect(locator).ToHaveCountAsync(expectedCount, new() { Timeout = 2_000 });
                return;
            }
            catch (PlaywrightException) when (DateTimeOffset.UtcNow < deadline)
            {
                // Projection not caught up yet — re-query.
            }
        }
    }
}
