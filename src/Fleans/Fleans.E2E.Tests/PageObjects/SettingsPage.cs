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

    public ILocator DiagramThemeSelect => _page.GetByTestId("settings-diagram-theme");

    public ILocator LanguageSelect => _page.GetByTestId("settings-language");

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

    public Task SelectThemeModeAsync(string mode, string? label = null) => SelectAsync(ThemeModeSelect, label ?? mode, mode);

    public async Task SelectLanguageAsync(string culture)
    {
        await LanguageSelect.ClickAsync();
        await LanguageSelect.Locator($"fluent-option[value='{culture}']").ClickAsync();
        await Expect(_page.Locator("html")).ToHaveAttributeAsync("lang", culture, new() { Timeout = 15_000 });
        await BlazorPage.WaitForInteractiveAsync(_page);
        await Expect(LanguageSelect).ToHaveJSPropertyAsync("value", culture);
    }

    public Task SelectAccentColorAsync(string color) => SelectAsync(AccentColorSelect, color);

    /// <summary>Picks the BPMN diagram style; <paramref name="value"/> is the option value (<c>classic</c> / <c>archify</c>).</summary>
    public async Task SelectDiagramThemeAsync(string label, string value)
    {
        await DiagramThemeSelect.ClickAsync();
        await DiagramThemeSelect.Locator("fluent-option", new() { HasTextString = label }).First.ClickAsync();
        await Expect(DiagramThemeSelect).ToHaveJSPropertyAsync("value", value);
    }

    /// <summary>Diagram style persisted by <c>js/bpmnTheme.js</c> under <c>localStorage["fleans.diagramTheme"]</c>.</summary>
    public Task<string?> ReadPersistedDiagramThemeAsync() =>
        _page.EvaluateAsync<string?>("() => window.localStorage.getItem('fleans.diagramTheme')");

    /// <summary>
    /// Waits for the diagram style to be persisted: the select shows the new value at once, but
    /// Settings.razor writes it through a server round-trip (JS interop) that lands a moment later.
    /// </summary>
    public Task WaitForPersistedDiagramThemeAsync(string value) =>
        _page.WaitForFunctionAsync(
            "v => window.localStorage.getItem('fleans.diagramTheme') === v", value,
            new() { Timeout = 10_000 });

    public async Task AssertThemeModeAsync(string mode) =>
        await Expect(ThemeModeSelect).ToHaveJSPropertyAsync("value", mode);

    public async Task AssertAccentColorAsync(string color) =>
        await Expect(AccentColorSelect).ToHaveJSPropertyAsync("value", color);

    /// <summary>Raw JSON FluentDesignTheme persisted under <c>localStorage["theme"]</c> (null if unset).</summary>
    public Task<string?> ReadPersistedThemeAsync() =>
        _page.EvaluateAsync<string?>("() => window.localStorage.getItem('theme')");

    private static async Task SelectAsync(ILocator select, string optionText, string? value = null)
    {
        await select.ClickAsync();
        await select.Locator("fluent-option", new() { HasTextString = optionText }).First.ClickAsync();
        await Expect(select).ToHaveJSPropertyAsync("value", value ?? optionText);
    }
}
