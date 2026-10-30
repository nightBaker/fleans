using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// #945 — real pointer/keyboard resizing, selection stability and responsive limits.
[TestClass]
[TestCategory("E2E")]
public class EditorInspectorResizeTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task Drag_ResizesInspectorAndCanvas_AndKeepsWidthWhenSelectionChanges()
    {
        var editor = await OpenEditorAsync();
        var beforeXml = await editor.GetXmlAsync();
        var handle = Page.GetByTestId("editor-inspector-resizer");
        var beforeWidth = await InspectorWidthAsync();
        var beforeCanvas = await Page.Locator(".editor-canvas").BoundingBoxAsync();
        var divider = (await handle.BoundingBoxAsync())!;
        await Expect(handle).ToHaveCSSAsync("cursor", "col-resize");

        await Page.Mouse.MoveAsync(divider.X + 4, divider.Y + 100);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(divider.X - 136, divider.Y + 100, new() { Steps = 8 });
        await Page.Mouse.UpAsync();

        var afterWidth = await InspectorWidthAsync();
        Assert.AreEqual(beforeWidth + 140, afterWidth, 1);
        var afterCanvas = await Page.Locator(".editor-canvas").BoundingBoxAsync();
        Assert.AreEqual(beforeCanvas!.Width - 140, afterCanvas!.Width, 1);
        await Page.WaitForFunctionAsync("""
            () => Math.abs(window.bpmnEditor._modeler.get('canvas').viewbox().outer.width
                - document.querySelector('.editor-canvas').clientWidth) < 1
            """);
        await Expect(Page.Locator("#editor-root")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("editor-page--resizing"));

        await Page.Locator(".djs-shape[data-element-id='end']").ClickAsync();
        Assert.AreEqual(afterWidth, await InspectorWidthAsync(), 1);
        await Page.Locator(".djs-shape[data-element-id='work']").ClickAsync();
        Assert.AreEqual(afterWidth, await InspectorWidthAsync(), 1);
        Assert.AreEqual(beforeXml, await editor.GetXmlAsync(), "Resizing must not change BPMN XML.");
    }

    [TestMethod]
    public async Task Keyboard_ClampsWidth_AndMobileLayoutDoesNotKeepTheDesktopWidth()
    {
        await OpenEditorAsync();
        var handle = Page.GetByTestId("editor-inspector-resizer");
        await handle.FocusAsync();
        await handle.PressAsync("Home");
        Assert.AreEqual(280, await InspectorWidthAsync(), 1);
        await handle.PressAsync("ArrowLeft");
        Assert.AreEqual(296, await InspectorWidthAsync(), 1);
        await handle.PressAsync("ArrowRight");
        Assert.AreEqual(280, await InspectorWidthAsync(), 1);
        await handle.PressAsync("ArrowRight");
        Assert.AreEqual(280, await InspectorWidthAsync(), 1, "The panel must not shrink below its minimum.");
        await handle.PressAsync("End");
        Assert.AreEqual(640, await InspectorWidthAsync(), 1);
        await handle.PressAsync("ArrowLeft");
        Assert.AreEqual(640, await InspectorWidthAsync(), 1, "The panel must not grow beyond its maximum.");

        await Page.SetViewportSizeAsync(1100, 900);
        await Page.WaitForFunctionAsync("""
            () => document.querySelector('.editor-inspector').getBoundingClientRect().width < 640
            """);
        var canvas = (await Page.Locator(".editor-canvas").BoundingBoxAsync())!;
        Assert.IsGreaterThanOrEqualTo(319, canvas.Width, "Resizing the window must preserve room for the canvas.");

        await Page.SetViewportSizeAsync(700, 900);
        await Expect(handle).ToBeHiddenAsync();
        var content = (await Page.Locator(".editor-content").BoundingBoxAsync())!;
        Assert.AreEqual(content.Width, await InspectorWidthAsync(), 1, "Mobile uses the full-width stacked panel.");

        await Page.SetViewportSizeAsync(1600, 1000);
        await Expect(handle).ToBeVisibleAsync();
        await Expect(handle).ToHaveAttributeAsync("aria-valuenow", "640");
        Assert.AreEqual(640, await InspectorWidthAsync(), 1);
    }

    [TestMethod]
    public async Task Width_PersistsAcrossDiagramTabs_AndXmlMode()
    {
        await OpenEditorAsync();
        var handle = Page.GetByTestId("editor-inspector-resizer");
        await handle.FocusAsync();
        await handle.PressAsync("Home");
        await handle.PressAsync("ArrowLeft");

        await Page.GetByRole(AriaRole.Button, new() { Name = "New Diagram", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".editor-tab")).ToHaveCountAsync(2);
        Assert.AreEqual(296, await InspectorWidthAsync(), 1);
        await Page.Locator(".editor-tab").First.ClickAsync();
        await Expect(Page.Locator(".djs-shape[data-element-id='work']")).ToHaveCountAsync(1);
        Assert.AreEqual(296, await InspectorWidthAsync(), 1);

        await Page.GetByTestId("editor-mode-xml").ClickAsync();
        await Expect(Page.Locator("#editor-xml")).ToBeVisibleAsync();
        await handle.FocusAsync();
        await handle.PressAsync("ArrowLeft");
        Assert.AreEqual(312, await InspectorWidthAsync(), 1);
        await Page.GetByTestId("editor-mode-modeling").ClickAsync();
        await Expect(Page.Locator(".editor-canvas")).ToBeVisibleAsync();
        Assert.AreEqual(312, await InspectorWidthAsync(), 1);
    }

    private async Task<EditorPage> OpenEditorAsync()
    {
        await Page.SetViewportSizeAsync(1600, 1000);
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(InlineBpmn.ScriptShapesOnly("resize-" + InlineBpmn.UniqueSuffix()));
        await Page.Locator(".djs-shape[data-element-id='work']").ClickAsync();
        await Expect(Page.Locator(".editor-inspector .properties-panel")).ToBeVisibleAsync();
        return editor;
    }

    private async Task<double> InspectorWidthAsync() =>
        (await Page.Locator(".editor-inspector").BoundingBoxAsync())!.Width;
}
