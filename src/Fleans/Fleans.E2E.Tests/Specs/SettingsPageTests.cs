using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Admin UI smoke: /settings (Settings.razor) — issue #772. Settings is client-only state
// (no API surface): the assertions target the persisted FluentDesignTheme value and the
// select state across in-app navigation and a full reload.
[TestClass]
[TestCategory("E2E")]
public class SettingsPageTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ThemeSelection_IsPersisted_AndSurvivesNavigationAndReload()
    {
        var page = new SettingsPage(Page);
        await page.OpenAsync();
        await page.AssertThemeModeAsync("System");
        await page.AssertAccentColorAsync("Default");

        await page.SelectThemeModeAsync("Dark");
        await page.SelectAccentColorAsync("Excel");

        // MainLayout's FluentDesignTheme persists the choice under localStorage["theme"].
        await Page.WaitForFunctionAsync(
            "() => (window.localStorage.getItem('theme') || '').toLowerCase().includes('dark')",
            options: new PageWaitForFunctionOptions { PollingInterval = 100, Timeout = 10_000 });

        // In-app navigation (same circuit) keeps the selection.
        await Page.Locator(".fluent-appbar-item a[href='/workflows']").ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(Page)
            .ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/workflows$"));
        await page.OpenViaHeaderLinkAsync();
        await page.AssertThemeModeAsync("Dark");
        await page.AssertAccentColorAsync("Excel");

        // A full reload (new circuit) restores the persisted theme into the selects.
        await page.OpenAsync();
        await page.AssertThemeModeAsync("Dark");
        await page.AssertAccentColorAsync("Excel");
        var persisted = await page.ReadPersistedThemeAsync();
        StringAssert.Contains(persisted?.ToLowerInvariant() ?? string.Empty, "dark");
    }
}
