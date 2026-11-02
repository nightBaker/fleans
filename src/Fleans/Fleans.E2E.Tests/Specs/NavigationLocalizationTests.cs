using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

[TestClass]
[TestCategory("E2E")]
public class NavigationLocalizationTests : WorkflowE2ETestBase
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1280, 720)]
    [DataRow(768, 800)]
    [DataRow(390, 844)]
    public async Task ShellLabelsAndNavigation_FollowLanguageAcrossReloads(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        var settings = new SettingsPage(Page);
        await settings.OpenAsync();
        await AssertNavigation(["Workflows", "Editor", "Deployments", "Custom Tasks", "Events"]);
        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Sections", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator(".orbit-brand")).ToHaveAttributeAsync("aria-label", "Fleans home");
        await Expect(Page.Locator("a[href='/settings']")).ToHaveAttributeAsync("title", "Settings");

        // Use an explicit mode so the button's label does not depend on OS dark-mode preference.
        await settings.SelectThemeModeAsync("Dark");
        await Expect(Page.GetByTestId("theme-toggle")).ToHaveAttributeAsync("aria-label", "Switch to light theme");
        await Page.WaitForFunctionAsync("() => (localStorage.getItem('theme') || '').toLowerCase().includes('dark')");
        await settings.SelectLanguageAsync("ru");

        await AssertNavigation(["Процессы", "Редактор", "Развёртывания", "Обработчики задач", "События"]);
        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Разделы", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator(".orbit-brand")).ToHaveAttributeAsync("aria-label", "Главная Fleans");
        var gear = Page.GetByRole(AriaRole.Link, new() { Name = "Настройки", Exact = true });
        await Expect(gear).ToHaveAttributeAsync("title", "Настройки");
        var toggle = Page.GetByTestId("theme-toggle");
        await Expect(toggle).ToHaveAttributeAsync("aria-label", "Включить светлую тему");
        await Expect(toggle).ToHaveAttributeAsync("title", "Включить светлую тему");
        await toggle.ClickAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-label", "Включить тёмную тему");
        await Expect(toggle).ToHaveAttributeAsync("title", "Включить тёмную тему");
        await Expect(Page.Locator("#blazor-error-ui")).ToContainTextAsync("Произошла непредвиденная ошибка.");
        await Expect(Page.Locator("#blazor-error-ui .reload")).ToHaveTextAsync("Перезагрузить");
        await Expect(Page.Locator("#blazor-error-ui .dismiss")).ToHaveAttributeAsync("title", "Закрыть");

        // The narrow-screen menu may scroll internally; the document itself must not overflow.
        Assert.IsTrue(await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth <= window.innerWidth + 1"));
        var screenshot = Path.Combine(TestContext.TestRunResultsDirectory!, $"navigation-ru-{width}.png");
        await Page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
        TestContext.AddResultFile(screenshot);

        await Page.GetByTestId("nav-events").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/events$"));
        await Expect(Page.GetByTestId("nav-events")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        await settings.OpenViaHeaderLinkAsync();
        await settings.OpenAsync();
        await AssertNavigation(["Процессы", "Редактор", "Развёртывания", "Обработчики задач", "События"]);
        await settings.SelectLanguageAsync("en");
        await AssertNavigation(["Workflows", "Editor", "Deployments", "Custom Tasks", "Events"]);
        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Sections", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator("a[href='/settings']")).ToHaveAttributeAsync("aria-label", "Settings");
    }

    private async Task AssertNavigation(string[] labels)
    {
        string[] ids = ["workflows", "editor", "deployments", "custom-tasks", "events"];
        string[] urls = ["/workflows", "/editor", "/deployments", "/admin/custom-tasks", "/events"];
        for (var i = 0; i < ids.Length; i++)
        {
            var link = Page.GetByTestId($"nav-{ids[i]}");
            await Expect(link).ToHaveTextAsync(labels[i]);
            await Expect(link).ToHaveAttributeAsync("href", urls[i]);
        }
    }
}
