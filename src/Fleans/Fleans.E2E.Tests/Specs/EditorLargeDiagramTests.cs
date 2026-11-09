using Fleans.E2E.Tests.ApiClient;
using System.Text.Json;
using System.Xml.Linq;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

[TestClass]
[TestCategory("E2E")]
public class EditorLargeDiagramTests : WorkflowE2ETestBase
{
    private static string LargeDiagram()
    {
        var key = "e2e-large-" + InlineBpmn.UniqueSuffix();
        var document = XDocument.Parse(BpmnFixtureLoader.Load("18-user-task", "user-task-approval.bpmn")
            .Replace("user-task-approval", key));
        XNamespace bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
        document.Descendants(bpmn + "process").Single().AddFirst(
            new XElement(bpmn + "documentation", new string('x', 40_000)));
        return document.ToString();
    }

    [TestMethod]
    public async Task LargeDiagram_DeploysThroughEditor()
    {
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(LargeDiagram());
        await Page.Locator(".editor-actions fluent-button", new() { HasTextString = "Deploy" }).ClickAsync();
        await Expect(Page.GetByTestId("deploy-dialog")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Page.GetByTestId("deploy-confirm").ClickAsync();
        await Expect(Page.GetByTestId("deploy-dialog")).ToContainTextAsync("Deployed e2e-large-", new() { Timeout = 15_000 });
    }

    [TestMethod]
    public async Task LargeDiagram_RestoresFromLocalStorage_AndSurvivesReload()
    {
        await Page.GotoAsync("/");
        var payload = JsonSerializer.Serialize(new
        {
            ActiveTabId = "large-tab",
            Tabs = new[] { new { Id = "large-tab", Label = "Large diagram", ProcessKey = "", BpmnXml = LargeDiagram(), Dirty = true } }
        });
        await Page.EvaluateAsync("json => localStorage.setItem('fleans.editor.tabs.v1', json)", payload);
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        StringAssert.Contains(await editor.GetXmlAsync(), new string('x', 40_000));
        await Page.ReloadAsync();
        await Page.Locator(".editor-page[data-editor-ready='true']").WaitForAsync(new() { Timeout = 15_000 });
        StringAssert.Contains(await editor.GetXmlAsync(), new string('x', 40_000));
    }
}
