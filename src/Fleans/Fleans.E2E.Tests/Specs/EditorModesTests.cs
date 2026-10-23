using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Editor modes (Editor.Modes.cs): the Simulation dry run and the History comparison, driven through the
// mode switch. Visual details (markers, badges, breakpoints) stay in tests/manual/73-orbit-admin-ui.
[TestClass]
[TestCategory("E2E")]
public class EditorModesTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task Simulation_WaitsAtGateway_ThenFinishesAfterChoosingABranch_AndLeavesNoTrace()
    {
        var xml = BpmnFixtureLoader.Load("03-exclusive-gateway", "conditional-branching.bpmn");
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(xml);
        var before = await editor.GetXmlAsync();

        await Page.GetByTestId("editor-mode-simulation").ClickAsync();
        var panel = Page.GetByTestId("editor-sim-panel");
        await Expect(panel.Locator("h2")).ToHaveTextAsync("Waiting for you", new() { Timeout = 15_000 });

        // One button per outgoing flow of the exclusive gateway.
        var options = panel.Locator(".orbit-decision fluent-button");
        await Expect(options).ToHaveCountAsync(2);
        await options.First.ClickAsync();
        await Expect(panel.Locator("h2")).ToHaveTextAsync("Run finished", new() { Timeout = 15_000 });

        // Read-only while simulating: Delete on a selected element changes nothing.
        await Page.Locator("[data-element-id='setX']").ClickAsync();
        await Page.Keyboard.PressAsync("Delete");
        await Expect(Page.Locator("[data-element-id='setX']")).ToHaveCountAsync(1);

        await Page.GetByTestId("editor-mode-modeling").ClickAsync();
        await Expect(Page.Locator(".orbit-sim-done, .orbit-sim-active, .orbit-sim-waiting")).ToHaveCountAsync(0);
        Assert.AreEqual(before, await editor.GetXmlAsync(), "A simulation run must not change the diagram.");
    }

    [TestMethod]
    public async Task History_ComparesTheTabWithTheVersionItWasOpenedFrom()
    {
        var key = "e2e-history-" + InlineBpmn.UniqueSuffix();
        var v1 = BpmnFixtureLoader.Load("03-exclusive-gateway", "conditional-branching.bpmn")
            .Replace("exclusive-gateway-test", key);
        await ApiClient.DeployAsync(v1);

        var editor = new EditorPage(Page);
        await Page.GotoAsync($"/editor/{key}/1");
        await Page.Locator(".editor-page[data-editor-ready='true']").WaitForAsync(new() { Timeout = 30_000 });

        // Same tab (still "opened from v1"), with one task renamed.
        await editor.LoadXmlAsync(v1.Replace("id=\"highTask\"", "id=\"highTask\" name=\"Renamed in E2E\""));

        await Page.GetByTestId("editor-mode-history").ClickAsync();
        var panel = Page.GetByTestId("editor-history-panel");
        await Expect(panel.Locator(".orbit-version")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".orbit-float--legend")).ToContainTextAsync("~ changed 1", new() { Timeout = 15_000 });
        await Expect(panel.Locator(".orbit-change--changed")).ToContainTextAsync("Renamed in E2E");
        await Expect(Page.Locator("[data-element-id='highTask'].orbit-diff-changed")).ToHaveCountAsync(1);
    }
}
