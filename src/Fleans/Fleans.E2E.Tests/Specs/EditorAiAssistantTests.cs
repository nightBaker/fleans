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
// #1017 — with TestHttpServer.AssistantStoresChats on, the same stub keeps chats, and a tab's chat
// comes back after a reload.
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

    [TestCleanup]
    public void BackendWithoutStoredChats() => TestHttpServer.AssistantStoresChats = false;

    [TestMethod]
    public async Task StoredChat_ComesBackAfterAReload_AndCanBeReopenedOrDeleted()
    {
        // The editor reads the backend's capabilities once per circuit: switch before opening it.
        TestHttpServer.AssistantStoresChats = true;
        var prompt = "stored approval " + InlineBpmn.UniqueSuffix();
        var panel = await AskAsync(prompt);
        await Expect(panel.GetByTestId("editor-ai-chats")).ToBeVisibleAsync();

        // A stored chat's request carries its id and only the new turn.
        var sent = TestHttpServer.AssistantRequests.Select(r => JsonDocument.Parse(r.Body).RootElement)
            .Single(r => r.GetProperty("messages")[0].GetProperty("text").GetString() == prompt);
        var chatId = sent.GetProperty("conversationId").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(chatId));
        Assert.AreEqual(1, sent.GetProperty("messages").GetArrayLength());

        // The tab keeps the chat's id in localStorage; the turns are read back from the backend.
        await Page.ReloadAsync();
        await Page.GetByTestId("editor-ai-toggle").ClickAsync();
        panel = Page.GetByTestId("editor-ai-panel");
        await Expect(panel.GetByTestId("editor-ai-message-user")).ToContainTextAsync(prompt);
        await Expect(panel.GetByTestId("editor-ai-proposal")).ToBeVisibleAsync();

        // A new chat empties the tab; the stored one is still in the list.
        await panel.GetByTestId("editor-ai-new-chat").ClickAsync();
        await Expect(panel.GetByTestId("editor-ai-message-user")).ToHaveCountAsync(0);
        await panel.GetByTestId("editor-ai-chat-list-toggle").ClickAsync();
        await panel.Locator($"[data-conversation-id='{chatId}']").ClickAsync();
        await Expect(panel.GetByTestId("editor-ai-message-user")).ToContainTextAsync(prompt);

        // Delete asks first, then leaves an empty, new chat.
        await panel.GetByTestId("editor-ai-delete-chat").ClickAsync();
        await panel.GetByTestId("editor-ai-delete-confirm-button").ClickAsync();
        await Expect(panel.GetByTestId("editor-ai-message-user")).ToHaveCountAsync(0);
        await Expect(panel.GetByTestId("editor-ai-delete-chat")).ToHaveCountAsync(0);
    }
}
