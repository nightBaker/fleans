using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// #944 — navigation highlights and dismissing the full assistant queue, not just its five-item summary.
[TestClass]
[TestCategory("E2E")]
public class EditorAssistantTests : WorkflowE2ETestBase
{
    [TestMethod]
    [DataRow("orbit")]
    [DataRow("classic")]
    [DataRow("archify")]
    public async Task Show_HighlightsTheElement_WithoutChangingTheDiagram(string theme)
    {
        await Context.AddInitScriptAsync($"localStorage.setItem('fleans.diagramTheme', '{theme}');");
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(AssistantDiagram(1));
        var before = await editor.GetXmlAsync();
        var assistant = Page.GetByTestId("editor-assistant");
        await Expect(assistant).ToContainTextAsync("Review 1");

        await assistant.GetByRole(AriaRole.Button, new() { Name = "Show", Exact = true }).ClickAsync();

        var highlighted = Page.Locator(".djs-shape[data-element-id='review1'].orbit-focus-highlight");
        await Expect(highlighted).ToHaveCountAsync(1);
        await Expect(highlighted.Locator(":scope > .djs-outline")).ToHaveCSSAsync("visibility", "visible");
        await Expect(highlighted.Locator(":scope > .djs-outline")).ToHaveCSSAsync("stroke-width", "3px");
        Assert.AreEqual(before, await editor.GetXmlAsync(), "Show must not change BPMN XML.");

        // The temporary cue goes away without losing the normal selection.
        await Expect(highlighted).ToHaveCountAsync(0, new() { Timeout = 10_000 });
        await Expect(Page.Locator(".djs-shape[data-element-id='review1'].selected")).ToHaveCountAsync(1);
    }

    [TestMethod]
    public async Task Later_DismissesOne_AndDismissAll_HidesEvenSuggestionsBeyondTheSummaryCap()
    {
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(AssistantDiagram(7));
        var before = await editor.GetXmlAsync();
        var assistant = Page.GetByTestId("editor-assistant");
        await Expect(assistant).ToContainTextAsync("Review 1");

        await assistant.GetByRole(AriaRole.Button, new() { Name = "Later", Exact = true }).ClickAsync();
        await Expect(assistant).ToContainTextAsync("Review 2");
        await assistant.GetByTestId("editor-assistant-dismiss-all").ClickAsync();
        await Expect(assistant).ToHaveCountAsync(0);

        // Force a fresh summary, so a delayed queue item cannot make this a false pass.
        await Page.EvaluateAsync("() => window.bpmnEditor._scheduleSummary()");
        await Page.WaitForFunctionAsync("() => window.bpmnEditor.getSummary().suggestions.length === 0");
        Assert.AreEqual(before, await editor.GetXmlAsync(), "Dismissal must not change BPMN XML.");
        var warnings = await Page.EvaluateAsync<int>("() => window.bpmnEditor.getSummary().problemCounts.warnings");
        Assert.IsGreaterThan(0, warnings, "Dismissing hints must not disable validation (the empty script still warns).");

        // Dismiss all is a snapshot of current hints, not a global off switch.
        await Page.EvaluateAsync("""
            () => {
                const modeler = window.bpmnEditor._modeler;
                const registry = modeler.get('elementRegistry');
                const factory = modeler.get('elementFactory');
                modeler.get('modeling').appendShape(registry.get('review7'),
                    factory.createShape({ type: 'bpmn:UserTask' }), { x: 1500, y: 300 });
            }
            """);
        await Expect(assistant).ToBeVisibleAsync();
    }

    private static string AssistantDiagram(int taskCount)
    {
        var tasks = string.Join("\n", Enumerable.Range(1, taskCount)
            .Select(i => $"<userTask id=\"review{i}\" name=\"Review {i}\" />"));
        // An unrelated warning ensures validation remains active after dismissing all hints.
        tasks += "\n<scriptTask id=\"emptyScript\" scriptFormat=\"csharp\" />";
        var ids = new[] { "start" }.Concat(Enumerable.Range(1, taskCount).Select(i => $"review{i}"))
            .Concat(new[] { "emptyScript", "end" }).ToArray();
        var flows = string.Join("\n", Enumerable.Range(0, ids.Length - 1)
            .Select(i => $"<sequenceFlow id=\"flow{i}\" sourceRef=\"{ids[i]}\" targetRef=\"{ids[i + 1]}\" />"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         id="Definitions_Assistant" targetNamespace="http://bpmn.io/schema/bpmn">
              <process id="assistant-{InlineBpmn.UniqueSuffix()}" isExecutable="true">
                <startEvent id="start" />
                {tasks}
                <endEvent id="end" />
                {flows}
              </process>
            </definitions>
            """;
    }
}
