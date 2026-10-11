using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.PageObjects;

/// <summary>
/// Wraps Fleans.Web's <c>/settings</c> page (<c>Settings.razor</c>): Theme Mode + Accent Color
/// selects backed by the per-circuit <c>ThemeService</c> and persisted by MainLayout's
/// <c>FluentDesignTheme StorageName="theme"</c> into <c>localStorage["theme"]</c>.
/// </summary>
public sealed class SettingsPage
{
    private readonly IPage _page;

    public SettingsPage(IPage page)
    {
        _page = page;
    }

    public ILocator ThemeModeSelect => _page.GetByTestId("settings-theme-mode");

    public ILocator AccentColorSelect => _page.GetByTestId("settings-accent-color");

    public async Task OpenAsync()
    {
        await BlazorPage.GotoInteractiveAsync(_page, "/settings");
        await Expect(ThemeModeSelect).ToBeVisibleAsync();
    }

    /// <summary>Opens the header gear link (client-side navigation, same circuit).</summary>
    public async Task OpenViaHeaderLinkAsync()
    {
        await _page.Locator("a[href='/settings']").ClickAsync();
        await Expect(ThemeModeSelect).ToBeVisibleAsync();
    }

    public Task SelectThemeModeAsync(string mode) => SelectAsync(ThemeModeSelect, mode);

    public Task SelectAccentColorAsync(string color) => SelectAsync(AccentColorSelect, color);

    public async Task AssertThemeModeAsync(string mode) =>
        await Expect(ThemeModeSelect).ToHaveJSPropertyAsync("value", mode);

    public async Task AssertAccentColorAsync(string color) =>
        await Expect(AccentColorSelect).ToHaveJSPropertyAsync("value", color);

    /// <summary>Raw JSON FluentDesignTheme persisted under <c>localStorage["theme"]</c> (null if unset).</summary>
    public Task<string?> ReadPersistedThemeAsync() =>
        _page.EvaluateAsync<string?>("() => window.localStorage.getItem('theme')");

    private static async Task SelectAsync(ILocator select, string optionText)
    {
        await select.ClickAsync();
        await select.Locator("fluent-option", new() { HasTextString = optionText }).First.ClickAsync();
        await Expect(select).ToHaveJSPropertyAsync("value", optionText);
    }
}
