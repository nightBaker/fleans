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
        await Page.GetByTestId("nav-workflows").ClickAsync();
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

    // Diagram Style (js/bpmnTheme.js): orbit by default, classic and archify opt-in. The theme is applied
    // when the editor builds its bpmn-js modeler, as a container class plus per-shape kind/category classes.
    [TestMethod]
    public async Task DiagramStyle_DefaultsToOrbit_AndArchifyAppliesToEditor()
    {
        const string bpmn = """
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                              xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                              id="d" targetNamespace="x">
              <bpmn:process id="diagram-style" isExecutable="true">
                <bpmn:userTask id="review" name="Review" />
                <bpmn:scriptTask id="calc" name="Calc" />
              </bpmn:process>
              <bpmndi:BPMNDiagram id="dg">
                <bpmndi:BPMNPlane id="p" bpmnElement="diagram-style">
                  <bpmndi:BPMNShape id="review_di" bpmnElement="review"><dc:Bounds x="100" y="100" width="100" height="80" /></bpmndi:BPMNShape>
                  <bpmndi:BPMNShape id="calc_di" bpmnElement="calc"><dc:Bounds x="260" y="100" width="100" height="80" /></bpmndi:BPMNShape>
                </bpmndi:BPMNPlane>
              </bpmndi:BPMNDiagram>
            </bpmn:definitions>
            """;

        var settings = new SettingsPage(Page);
        await settings.OpenAsync();
        await Microsoft.Playwright.Assertions.Expect(settings.DiagramThemeSelect).ToHaveJSPropertyAsync("value", "orbit");

        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(bpmn);
        await Microsoft.Playwright.Assertions.Expect(Page.Locator(".djs-container.fl-diagram-orbit")).ToHaveCountAsync(1);
        await Microsoft.Playwright.Assertions.Expect(Page.Locator(".fl-diagram-archify")).ToHaveCountAsync(0);
        // Orbit kind classes + the mono caption its renderer draws on tasks.
        await Microsoft.Playwright.Assertions.Expect(Page.Locator("[data-element-id='review'].o-kind-task .o-kind-label")).ToHaveTextAsync("USER");
        await Microsoft.Playwright.Assertions.Expect(Page.Locator("[data-element-id='calc'].o-kind-task .o-kind-label")).ToHaveTextAsync("SCRIPT");

        await settings.OpenAsync();
        await settings.SelectDiagramThemeAsync("Archify", "archify");
        await settings.WaitForPersistedDiagramThemeAsync("archify");
        Assert.AreEqual("archify", await settings.ReadPersistedDiagramThemeAsync());

        await editor.OpenAsync();
        await editor.LoadXmlAsync(bpmn);
        await Microsoft.Playwright.Assertions.Expect(Page.Locator(".djs-container.fl-diagram-archify")).ToHaveCountAsync(1);
        await Microsoft.Playwright.Assertions.Expect(Page.Locator("[data-element-id='review'].fl-cat-human")).ToHaveCountAsync(1);
        await Microsoft.Playwright.Assertions.Expect(Page.Locator("[data-element-id='calc'].fl-cat-compute")).ToHaveCountAsync(1);

        // A full reload restores the stored choice into the select.
        await settings.OpenAsync();
        await Microsoft.Playwright.Assertions.Expect(settings.DiagramThemeSelect).ToHaveJSPropertyAsync("value", "archify");
    }
}
