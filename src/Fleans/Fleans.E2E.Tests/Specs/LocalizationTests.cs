using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// #971: culture must survive a new HTTP request and Blazor circuit, remain browser-scoped,
// and fall back to the neutral English resource for keys absent from the Russian satellite.
[TestClass]
[TestCategory("E2E")]
public class LocalizationTests : WorkflowE2ETestBase
{
    private const string CookieName = ".AspNetCore.Culture";
    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public async Task CaptureFailureAsync()
    {
        if (TestContext.CurrentTestOutcome == UnitTestOutcome.Failed && !Page.IsClosed)
            await SaveScreenshot($"settings-failure-{Guid.NewGuid():N}.png");
    }

    [TestMethod]
    public async Task LanguageSelection_PersistsAcrossNavigationAndReload_WithoutChangingStoredThemeValues()
    {
        var settings = new SettingsPage(Page);
        await settings.OpenAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Settings", Exact = true })).ToBeVisibleAsync();
        await settings.SelectThemeModeAsync("Dark");
        await WaitForStoredTheme("dark");
        var previousTheme = await settings.ReadPersistedThemeAsync();
        await settings.SelectAccentColorAsync("Excel");
        // The select updates before FluentDesignTheme's browser-storage round-trip. Wait
        // for the accent to be persisted as well, before the language switch reloads the page.
        await Page.WaitForFunctionAsync("previous => localStorage.getItem('theme') !== previous", previousTheme);
        await settings.SelectDiagramThemeAsync("Classic", "classic");
        await settings.WaitForPersistedDiagramThemeAsync("classic");

        await settings.SelectLanguageAsync("ru");

        await Expect(Page).ToHaveTitleAsync("Настройки");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Настройки", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Внешний вид", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Тема оформления", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Акцентный цвет", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Стиль диаграммы", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Язык", new() { Exact = true })).ToBeVisibleAsync();
        await settings.AssertThemeModeAsync("Dark");
        await settings.AssertAccentColorAsync("Excel");
        await Expect(settings.DiagramThemeSelect).ToHaveJSPropertyAsync("value", "classic");
        // Brand names intentionally exist only in Settings.resx: this checks real English fallback.
        await Expect(settings.DiagramThemeSelect.Locator("fluent-option[value='orbit']")).ToHaveTextAsync("Orbit");
        await Expect(settings.AccentColorSelect.Locator("fluent-option[value='Excel']")).ToHaveTextAsync("Excel");
        await SaveScreenshot("settings-ru.png");

        var cookie = (await Context.CookiesAsync()).Single(c => c.Name == CookieName);
        StringAssert.Contains(Uri.UnescapeDataString(cookie.Value), "c=ru|uic=ru");
        Assert.IsTrue(cookie.HttpOnly);
        Assert.IsTrue(cookie.Secure);
        Assert.IsTrue(cookie.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await settings.SelectThemeModeAsync("Light", "Светлая");
        await WaitForStoredTheme("light");
        await Page.GetByTestId("nav-workflows").ClickAsync();
        await settings.OpenViaHeaderLinkAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Настройки", Exact = true })).ToBeVisibleAsync();
        await settings.OpenAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "ru");
        await settings.AssertThemeModeAsync("Light");
        await settings.AssertAccentColorAsync("Excel");
        await Expect(settings.DiagramThemeSelect).ToHaveJSPropertyAsync("value", "classic");

        await settings.SelectLanguageAsync("en");
        await Expect(Page).ToHaveTitleAsync("Settings");
        await settings.AssertThemeModeAsync("Light");
        await SaveScreenshot("settings-en.png");
    }

    [TestMethod]
    public async Task CultureIsBrowserScoped_AndEnglishIsDefaultEvenWithRussianBrowserHeaders()
    {
        var settings = new SettingsPage(Page);
        await settings.OpenAsync();
        await settings.SelectLanguageAsync("ru");
        await using var otherContext = await AspireFixture.Browser.NewContextAsync(new()
        {
            BaseURL = AspireFixture.WebBaseUri.ToString(), IgnoreHTTPSErrors = true, Locale = "ru-RU"
        });
        var other = await otherContext.NewPageAsync();

        await other.GotoAsync("/settings?culture=ru&ui-culture=ru");

        await Expect(other.GetByRole(AriaRole.Heading, new() { Name = "Settings", Exact = true })).ToBeVisibleAsync();
        await Expect(other.Locator("html")).ToHaveAttributeAsync("lang", "en");
        Assert.IsFalse((await otherContext.CookiesAsync()).Any(c => c.Name == CookieName));
        await settings.OpenAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "ru");
    }

    [TestMethod]
    public async Task UnsupportedCookie_FallsBackToEnglish()
    {
        await SetCookie("de");

        await new SettingsPage(Page).OpenAsync();

        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "en");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Settings", Exact = true })).ToBeVisibleAsync();
    }

    [TestMethod]
    [DataRow("fr")]
    [DataRow("ru-RU")]
    public async Task CultureEndpoint_RejectsUnsupportedLanguages_WithoutChangingTheCookie(string culture)
    {
        await SetCookie("ru");
        await new SettingsPage(Page).OpenAsync();
        var url = new Uri(new Uri(Page.Url), $"/Culture/Set?culture={Uri.EscapeDataString(culture)}&returnUrl=/settings");

        var response = await Context.APIRequest.GetAsync(url.ToString(), new() { MaxRedirects = 0 });

        Assert.AreEqual(400, response.Status);
        var cookie = (await Context.CookiesAsync()).Single(c => c.Name == CookieName);
        StringAssert.Contains(Uri.UnescapeDataString(cookie.Value), "c=ru|uic=ru");
        await response.DisposeAsync();
    }

    [TestMethod]
    [DataRow("https://example.com/", "/settings")]
    [DataRow("//example.com/", "/settings")]
    [DataRow("/\\example.com/", "/settings")]
    [DataRow("/settings?section=appearance", "/settings?section=appearance")]
    public async Task CultureEndpoint_RedirectsOnlyToLocalUrls(string returnUrl, string expected)
    {
        await new SettingsPage(Page).OpenAsync();
        var url = new Uri(new Uri(Page.Url), $"/Culture/Set?culture=ru&returnUrl={Uri.EscapeDataString(returnUrl)}");

        var response = await Context.APIRequest.GetAsync(url.ToString(), new() { MaxRedirects = 0 });

        Assert.AreEqual(302, response.Status);
        Assert.AreEqual(expected, response.Headers["location"]);
        await response.DisposeAsync();
    }

    [TestMethod]
    public async Task DiagramStorageFailure_ShowsRussianMessage()
    {
        await SetCookie("ru");
        await Page.AddInitScriptAsync("""
            const originalSetItem = Storage.prototype.setItem;
            Storage.prototype.setItem = function(key, value) {
                if (key === 'fleans.diagramTheme') throw new DOMException('Storage unavailable', 'QuotaExceededError');
                return originalSetItem.call(this, key, value);
            };
            """);
        var settings = new SettingsPage(Page);
        await settings.OpenAsync();

        await settings.SelectDiagramThemeAsync("Классический", "classic");

        await Expect(Page.GetByTestId("settings-diagram-theme-error"))
            .ToHaveTextAsync("Не удалось сохранить стиль диаграммы: хранилище браузера недоступно.");
    }

    private Task SetCookie(string culture) => Context.AddCookiesAsync([new Cookie
    {
        Name = CookieName, Value = $"c={culture}|uic={culture}", Url = AspireFixture.WebBaseUri.ToString()
    }]);

    private Task WaitForStoredTheme(string mode) => Page.WaitForFunctionAsync(
        "mode => (localStorage.getItem('theme') || '').toLowerCase().includes(mode)", mode);

    private async Task SaveScreenshot(string filename)
    {
        var path = Path.Combine(TestContext.TestRunResultsDirectory!, filename);
        await Page.ScreenshotAsync(new() { Path = path, FullPage = true });
        TestContext.AddResultFile(path);
    }
}
