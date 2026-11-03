using System.Text.Json;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/78-editor-ai-assistant (scenarios 2 and 3). #1015 — the AI assistant panel
// against a stub backend (TestHttpServer's POST /assistant/chat), which streams a reply and a
// layout-free BPMN proposal. Error paths and the fix loop are unit-tested in Fleans.Web.Tests.
[TestClass]
[TestCategory("E2E")]
public class EditorAiAssistantTests : WorkflowE2ETestBase
{
    private async Task<ILocator> AskAsync(string prompt)
    {
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await Page.GetByTestId("editor-ai-toggle").ClickAsync();
        var panel = Page.GetByTestId("editor-ai-panel");
        await panel.GetByTestId("editor-ai-input").Locator("textarea").FillAsync(prompt);
        await panel.GetByTestId("editor-ai-send").ClickAsync();
        await Expect(panel.GetByTestId("editor-ai-proposal")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        return panel;
    }

    [TestMethod]
    public async Task GeneratedDiagram_OpensInANewTab_AndRenders()
    {
        var prompt = "approval process " + InlineBpmn.UniqueSuffix();
        var panel = await AskAsync(prompt);
        await Expect(panel.GetByTestId("editor-ai-message-assistant")).ToContainTextAsync(TestHttpServer.AssistantReplyText);
        var tabsBefore = await Page.Locator(".editor-tab").CountAsync();

        await panel.GetByTestId("editor-ai-open-new-tab").ClickAsync();

        await Expect(Page.Locator(".editor-tab")).ToHaveCountAsync(tabsBefore + 1);
        await Expect(Page.Locator(".editor-tab--active .editor-tab__label")).ToHaveTextAsync(TestHttpServer.AssistantProcessId);
        // The proposal has no diagram layout: bpmnAutoLayout draws every element and flow.
        foreach (var id in new[] { "start", "approve", "end", "f1", "f2" })
            await Expect(Page.Locator($".djs-element[data-element-id='{id}']")).ToHaveCountAsync(1);
        // The new tab keeps the conversation, so a follow-up can refer to it.
        await Expect(Page.GetByTestId("editor-ai-panel").GetByTestId("editor-ai-message-user")).ToContainTextAsync(prompt);

        // What the backend received: the prompt, the engine version and the plugin catalog.
        var sent = TestHttpServer.AssistantRequests.Select(r => JsonDocument.Parse(r.Body).RootElement)
            .Single(r => r.GetProperty("messages")[0].GetProperty("text").GetString() == prompt);
        Assert.IsFalse(string.IsNullOrEmpty(sent.GetProperty("engineVersion").GetString()));
        Assert.AreEqual(JsonValueKind.Array, sent.GetProperty("plugins").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, sent.GetProperty("currentXml").ValueKind, "a blank tab is sent as a new diagram");
    }

    [TestMethod]
    public async Task ReplaceCurrent_AsksFirst_ThenLoadsTheProposalIntoTheSameTab()
    {
        var panel = await AskAsync("approval process " + InlineBpmn.UniqueSuffix());
        var tabsBefore = await Page.Locator(".editor-tab").CountAsync();

        await panel.GetByTestId("editor-ai-replace").ClickAsync();
        await Expect(panel.GetByTestId("editor-ai-replace-confirm")).ToBeVisibleAsync();
        await Expect(Page.Locator(".djs-element[data-element-id='approve']")).ToHaveCountAsync(0);
        await panel.GetByTestId("editor-ai-replace-confirm-button").ClickAsync();

        await Expect(Page.Locator(".djs-element[data-element-id='approve']")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".editor-tab")).ToHaveCountAsync(tabsBefore);
        await Expect(Page.Locator(".editor-tab--active .editor-tab__dirty")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".process-id-toolbar")).ToHaveTextAsync(TestHttpServer.AssistantProcessId);
    }
}
