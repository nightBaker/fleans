using System.Text.Json;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/78-editor-ai-assistant (scenarios 2 and 3). #1015 — the AI assistant panel
// against a stub AG-UI backend (TestHttpServer's POST /assistant/agent, #1054), which streams a reply and a
// layout-free BPMN proposal. Error paths and the fix loop are unit-tested in Fleans.Web.Tests.
// #1017 — with TestHttpServer.AssistantStoresChats on, the same stub keeps chats, and a tab's chat
// comes back after a reload. #1024 — proposals apply directly, and History brings back any earlier state.
// The stub always proposes the same diagram; the history model itself is unit-tested.
[TestClass]
[TestCategory("E2E")]
public class EditorAiAssistantTests : WorkflowE2ETestBase
{
    private async Task<ILocator> AskAsync(string prompt)
    {
        // An applied proposal leaves a draft tab, so a reload asks through beforeunload: answer yes.
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await Page.GetByTestId("editor-ai-toggle").ClickAsync();
        var panel = Page.GetByTestId("editor-ai-panel");
        await SendAsync(panel, prompt);
        return panel;
    }

    private static async Task SendAsync(ILocator panel, string prompt)
    {
        await panel.GetByTestId("editor-ai-input").Locator("textarea").FillAsync(prompt);
        await panel.GetByTestId("editor-ai-send").ClickAsync();
        await Expect(panel.GetByTestId("editor-ai-proposal").Last).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    private async Task TurnOffAutoApplyAsync()
    {
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await Page.GetByTestId("editor-ai-toggle").ClickAsync();
        await Page.GetByTestId("editor-ai-auto-apply").ClickAsync();
        await Expect(Page.GetByTestId("editor-ai-auto-apply")).ToHaveAttributeAsync("aria-checked", "false");
    }

    private ILocator ProposedTask => Page.Locator(".djs-element[data-element-id='approve']");

    /// <summary>Applying shows the diagram first and saves the history and the tabs just after; a reload must wait for both.</summary>
    private async Task ReloadAfterTheProposalIsSavedAsync()
    {
        await Page.WaitForFunctionAsync("Object.keys(sessionStorage).some(k => k.startsWith('fleans.editor.assistant.history.'))");
        await Page.WaitForFunctionAsync("(localStorage.getItem('fleans.editor.tabs.v1') ?? '').includes('approve')");
        await new EditorPage(Page).ReloadAsync();
    }

    private static ILocator HistoryEntries(ILocator panel) => panel.GetByTestId("editor-ai-history-entry");

    private static ILocator HistoryEntry(ILocator panel, string kind) => panel.Locator($"[data-revision-kind='{kind}']");

    private async Task EditByHandAsync() =>
        await Page.EvaluateAsync("bpmnEditor.addElementAtCenter('user-task')");


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

        // What the backend received: an AG-UI run with the prompt, the engine version, the plugin catalog and the proposal tool.
        var sent = SentRun(prompt);
        var context = sent.GetProperty("context").EnumerateArray()
            .ToDictionary(c => c.GetProperty("description").GetString()!, c => c.GetProperty("value").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(context["fleans.engineVersion"]));
        Assert.AreEqual(JsonValueKind.Array, JsonDocument.Parse(context["fleans.plugins"]!).RootElement.ValueKind);
        Assert.AreEqual("propose_bpmn_diagram", sent.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.IsFalse(sent.GetProperty("state").TryGetProperty("bpmnXml", out _), "a blank tab is sent as a new diagram");
    }

    [TestMethod]
    public async Task ValidProposal_IsAppliedToTheOpenTab_WithoutADialog()
    {
        var panel = await AskAsync("approval process " + InlineBpmn.UniqueSuffix());
        var tabs = await Page.Locator(".editor-tab").CountAsync();

        await Expect(ProposedTask).ToHaveCountAsync(1);
        await Expect(panel.GetByTestId("editor-ai-applied")).ToBeVisibleAsync();
        await Expect(panel.GetByRole(AriaRole.Alertdialog)).ToHaveCountAsync(0);
        await Expect(Page.Locator(".editor-tab")).ToHaveCountAsync(tabs);
        await Expect(Page.Locator(".editor-tab--active .editor-tab__dirty")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".process-id-toolbar")).ToHaveTextAsync(TestHttpServer.AssistantProcessId);
        // Open in new tab stays available on an applied proposal.
        await Expect(panel.GetByTestId("editor-ai-open-new-tab")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task UndoLastChange_BringsBackThePreviousDiagram_AndTheProposalCanBeAppliedAgain()
    {
        var panel = await AskAsync("approval process " + InlineBpmn.UniqueSuffix());
        await Expect(ProposedTask).ToHaveCountAsync(1);

        await panel.GetByTestId("editor-ai-undo").ClickAsync();

        await Expect(ProposedTask).ToHaveCountAsync(0);
        await Expect(panel.GetByTestId("editor-ai-applied")).ToHaveCountAsync(0);
        await Expect(panel.GetByTestId("editor-ai-undo")).ToHaveCountAsync(0); // back at the first state

        await panel.GetByTestId("editor-ai-apply").ClickAsync();

        await Expect(ProposedTask).ToHaveCountAsync(1);
        await Expect(panel.GetByTestId("editor-ai-applied")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task ManualEdits_ThenARestore_KeepsTheManualStateInHistory()
    {
        var panel = await AskAsync("approval process " + InlineBpmn.UniqueSuffix());
        await Expect(ProposedTask).ToHaveCountAsync(1);
        var elements = await Page.Locator(".djs-shape").CountAsync();
        await EditByHandAsync();
        await Expect(Page.Locator(".djs-shape")).ToHaveCountAsync(elements + 1);

        await panel.GetByTestId("editor-ai-history-toggle").ClickAsync();
        await HistoryEntry(panel, "Original").GetByTestId("editor-ai-restore").ClickAsync();

        await Expect(ProposedTask).ToHaveCountAsync(0);
        // Original, the proposal, the hand edits and the restore, newest first.
        await Expect(HistoryEntries(panel)).ToHaveCountAsync(4);
        await Expect(HistoryEntries(panel).First).ToHaveAttributeAsync("data-revision-kind", "Restored");
        await Expect(HistoryEntries(panel).First.GetByTestId("editor-ai-history-current")).ToBeVisibleAsync();

        // The restored state can itself be left and the hand edits brought back.
        await HistoryEntry(panel, "Manual").GetByTestId("editor-ai-restore").ClickAsync();

        await Expect(ProposedTask).ToHaveCountAsync(1);
        await Expect(Page.Locator(".djs-shape")).ToHaveCountAsync(elements + 1);
        await Expect(HistoryEntries(panel)).ToHaveCountAsync(5);
    }

    [TestMethod]
    public async Task History_SurvivesAReload_ButNotANewBrowserSession()
    {
        TestHttpServer.AssistantStoresChats = true;
        var panel = await AskAsync("stored approval " + InlineBpmn.UniqueSuffix());
        await Expect(ProposedTask).ToHaveCountAsync(1);

        await ReloadAfterTheProposalIsSavedAsync();
        await Page.GetByTestId("editor-ai-toggle").ClickAsync();
        panel = Page.GetByTestId("editor-ai-panel");

        // The chat came back from the backend, the history from sessionStorage, and they still match.
        await Expect(panel.GetByTestId("editor-ai-applied")).ToBeVisibleAsync();
        await panel.GetByTestId("editor-ai-history-toggle").ClickAsync();
        await Expect(HistoryEntries(panel)).ToHaveCountAsync(2);
        await Expect(panel.GetByTestId("editor-ai-history-not-saved")).ToHaveCountAsync(0);

        // An untouched diagram isn't taken for a hand edit after the re-import: undo adds only the restore.
        await panel.GetByTestId("editor-ai-undo").ClickAsync();
        await Expect(ProposedTask).ToHaveCountAsync(0);
        await Expect(HistoryEntries(panel)).ToHaveCountAsync(3);
        await Expect(HistoryEntry(panel, "Manual")).ToHaveCountAsync(0);

        // A new page shares localStorage (the tabs) but starts its own sessionStorage.
        var other = await Page.Context.NewPageAsync();
        await new EditorPage(other).OpenAsync();
        await other.GetByTestId("editor-ai-toggle").ClickAsync();
        await Expect(other.GetByTestId("editor-ai-panel")).ToBeVisibleAsync();
        await Expect(other.GetByTestId("editor-ai-history-toggle")).ToHaveCountAsync(0);
        await other.CloseAsync();
    }

    [TestMethod]
    public async Task ClosingATab_RemovesItsHistory()
    {
        await AskAsync("approval process " + InlineBpmn.UniqueSuffix());
        await Expect(ProposedTask).ToHaveCountAsync(1);
        const string stored = "Object.keys(sessionStorage).filter(k => k.startsWith('fleans.editor.assistant.history.')).length";
        await Page.WaitForFunctionAsync(stored + " === 1");

        await Page.Locator(".editor-tab--active .editor-tab__close").ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Discard" }).ClickAsync();

        await Page.WaitForFunctionAsync(stored + " === 0");
    }

    [TestMethod]
    public async Task QuotaFailure_WarnsInThePanel_AndHistoryKeepsWorking()
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        // Every sessionStorage write is refused from here on, as with a full quota.
        await Page.EvaluateAsync("() => { Storage.prototype.setItem = function () { throw new DOMException('full', 'QuotaExceededError'); }; }");
        await Page.GetByTestId("editor-ai-toggle").ClickAsync();
        var panel = Page.GetByTestId("editor-ai-panel");

        await SendAsync(panel, "approval process " + InlineBpmn.UniqueSuffix());

        await Expect(ProposedTask).ToHaveCountAsync(1);
        await Expect(panel.GetByTestId("editor-ai-history-not-saved")).ToBeVisibleAsync();
        await panel.GetByTestId("editor-ai-undo").ClickAsync();
        await Expect(ProposedTask).ToHaveCountAsync(0);
    }

    [TestMethod]
    public async Task AutoApplyOff_ShowsTheOldButtons_AndReplaceKeepsEarlierStates()
    {
        await TurnOffAutoApplyAsync();
        var panel = Page.GetByTestId("editor-ai-panel");
        await SendAsync(panel, "approval process " + InlineBpmn.UniqueSuffix());
        var tabsBefore = await Page.Locator(".editor-tab").CountAsync();

        // Nothing changed until asked.
        await Expect(ProposedTask).ToHaveCountAsync(0);
        await Expect(panel.GetByTestId("editor-ai-open-new-tab")).ToBeVisibleAsync();

        await panel.GetByTestId("editor-ai-replace").ClickAsync();

        // No confirmation any more: the earlier state is in History.
        await Expect(ProposedTask).ToHaveCountAsync(1);
        await Expect(Page.Locator(".editor-tab")).ToHaveCountAsync(tabsBefore);
        await Expect(Page.Locator(".editor-tab--active .editor-tab__dirty")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".process-id-toolbar")).ToHaveTextAsync(TestHttpServer.AssistantProcessId);

        await panel.GetByTestId("editor-ai-undo").ClickAsync();
        await Expect(ProposedTask).ToHaveCountAsync(0);

        // The switch is remembered.
        await new EditorPage(Page).ReloadAsync();
        await Page.GetByTestId("editor-ai-toggle").ClickAsync();
        await Expect(Page.GetByTestId("editor-ai-auto-apply")).ToHaveAttributeAsync("aria-checked", "false");
    }

    [TestMethod]
    public async Task OpenInNewTab_StartsTheNewTabsHistoryWithTheProposal()
    {
        var panel = await AskAsync("approval process " + InlineBpmn.UniqueSuffix());
        await panel.GetByTestId("editor-ai-open-new-tab").ClickAsync();
        await Expect(Page.Locator(".editor-tab--active .editor-tab__label")).ToHaveTextAsync(TestHttpServer.AssistantProcessId);

        panel = Page.GetByTestId("editor-ai-panel");
        await panel.GetByTestId("editor-ai-history-toggle").ClickAsync();
        await Expect(HistoryEntries(panel)).ToHaveCountAsync(1);
        await Expect(HistoryEntries(panel).First).ToHaveAttributeAsync("data-revision-kind", "Assistant");
        await Expect(panel.GetByTestId("editor-ai-applied")).ToBeVisibleAsync();
    }

    [TestMethod]
    [DataRow(390, 844)]
    [DataRow(768, 1024)]
    [DataRow(1280, 720)]
    public async Task HistoryList_FitsThePanel_AndWorksFromTheKeyboard(int width, int height)
    {
        // At 768px and below the editor is view-only and hides the toolbar button, so the panel gets there
        // only by narrowing a window it is already open in.
        var panel = await AskAsync("approval process " + InlineBpmn.UniqueSuffix());
        await panel.GetByTestId("editor-ai-history-toggle").ClickAsync();
        await Expect(HistoryEntries(panel)).ToHaveCountAsync(2);
        await Page.SetViewportSizeAsync(width, height);

        var overflow = await panel.EvaluateAsync<bool>("el => el.scrollWidth > el.clientWidth + 1");
        Assert.IsFalse(overflow, $"the panel scrolls sideways at {width}px");

        var restore = panel.GetByTestId("editor-ai-restore").First;
        await restore.FocusAsync();
        await Expect(restore).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(ProposedTask).ToHaveCountAsync(0);
        await Expect(HistoryEntries(panel)).ToHaveCountAsync(3);
    }

    [TestMethod]
    public async Task ProposalArrivingInXmlMode_WaitsWithApply_UntilBackInModeling()
    {
        Interlocked.Exchange(ref TestHttpServer.AssistantReplyDelayTicks, TimeSpan.FromSeconds(3).Ticks);
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await Page.GetByTestId("editor-ai-toggle").ClickAsync();
        var panel = Page.GetByTestId("editor-ai-panel");
        await panel.GetByTestId("editor-ai-input").Locator("textarea").FillAsync("approval process " + InlineBpmn.UniqueSuffix());
        await panel.GetByTestId("editor-ai-send").ClickAsync();
        await Page.GetByTestId("editor-mode-xml").ClickAsync();

        await Expect(panel.GetByTestId("editor-ai-proposal")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(panel.GetByTestId("editor-ai-apply")).ToHaveAttributeAsync("disabled", string.Empty);
        await Expect(panel.GetByTestId("editor-ai-applied")).ToHaveCountAsync(0);

        await Page.GetByTestId("editor-mode-modeling").ClickAsync();
        await Expect(ProposedTask).ToHaveCountAsync(0);
        await panel.GetByTestId("editor-ai-apply").ClickAsync();

        await Expect(ProposedTask).ToHaveCountAsync(1);
        await Expect(panel.GetByTestId("editor-ai-applied")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task History_IsPerTab_AndShowMessageFindsTheProposal()
    {
        var panel = await AskAsync("approval process " + InlineBpmn.UniqueSuffix());
        await Expect(panel.GetByTestId("editor-ai-applied")).ToBeVisibleAsync();
        await panel.GetByTestId("editor-ai-history-toggle").ClickAsync();

        await panel.GetByTestId("editor-ai-history-show-message").ClickAsync();
        await Expect(panel.GetByTestId("editor-ai-message-assistant")).ToBeFocusedAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "New Diagram" }).ClickAsync();
        await Expect(Page.Locator(".editor-tab")).ToHaveCountAsync(2);
        await Expect(panel.GetByTestId("editor-ai-history-toggle")).ToHaveCountAsync(0);

        await Page.Locator(".editor-tab").First.ClickAsync();
        await Expect(panel.GetByTestId("editor-ai-history-toggle")).ToBeVisibleAsync();
        await Expect(ProposedTask).ToHaveCountAsync(1);
    }

    [TestMethod]
    public async Task RevisionCap_KeepsFifty_AndSaysTheOldestWereDropped()
    {
        var panel = await AskAsync("approval process " + InlineBpmn.UniqueSuffix());
        await Expect(panel.GetByTestId("editor-ai-applied")).ToBeVisibleAsync();

        // Starts with 2 entries; each click adds one, and undo and apply take turns.
        for (var added = 0; added < 50; added++)
        {
            var (click, then) = added % 2 == 0 ? ("editor-ai-undo", "editor-ai-apply") : ("editor-ai-apply", "editor-ai-undo");
            await panel.GetByTestId(click).ClickAsync();
            await Expect(panel.GetByTestId(then)).ToBeVisibleAsync();
        }

        var toggle = panel.GetByTestId("editor-ai-history-toggle");
        await Expect(toggle).ToHaveTextAsync("History (50)");
        await toggle.ClickAsync();
        await Expect(HistoryEntries(panel)).ToHaveCountAsync(50);
        await Expect(panel.GetByTestId("editor-ai-history-dropped")).ToContainTextAsync("2 oldest revisions");
    }

    [TestMethod]
    public async Task HistoryTexts_AreRussian_WithARussianCulture()
    {
        await Context.AddCookiesAsync([new Cookie
        {
            Name = ".AspNetCore.Culture", Value = "c=ru|uic=ru", Url = AspireFixture.WebBaseUri.ToString(),
        }]);
        var panel = await AskAsync("процесс согласования " + InlineBpmn.UniqueSuffix());

        await Expect(panel.GetByTestId("editor-ai-auto-apply")).ToContainTextAsync("Применять изменения автоматически");
        await Expect(panel.GetByTestId("editor-ai-applied")).ToHaveTextAsync("Применено к этой вкладке");
        await Expect(panel.GetByTestId("editor-ai-undo")).ToHaveTextAsync("Отменить последнее изменение");
        await panel.GetByTestId("editor-ai-history-toggle").ClickAsync();
        await Expect(panel.GetByTestId("editor-ai-history-toggle")).ToHaveTextAsync("История (2)");
        await Expect(HistoryEntry(panel, "Assistant")).ToContainTextAsync("Изменение ассистента");
        await Expect(HistoryEntry(panel, "Assistant")).ToContainTextAsync("Показать сообщение");
        await Expect(HistoryEntry(panel, "Assistant").GetByTestId("editor-ai-history-current")).ToHaveTextAsync("Текущая");
        await Expect(HistoryEntry(panel, "Original")).ToContainTextAsync("До изменений");
        await Expect(HistoryEntry(panel, "Original").GetByTestId("editor-ai-restore")).ToHaveTextAsync("Восстановить");

        await panel.GetByTestId("editor-ai-undo").ClickAsync();
        await Expect(HistoryEntry(panel, "Restored")).ToContainTextAsync("Восстановлена ревизия");
        await Expect(panel.GetByTestId("editor-ai-apply")).ToHaveTextAsync("Применить");
    }

    [TestCleanup]
    public void DefaultStubBackend()
    {
        TestHttpServer.AssistantStoresChats = false;
        TestHttpServer.AssistantAsksForApproval = false;
        Interlocked.Exchange(ref TestHttpServer.AssistantReplyDelayTicks, 0);
    }

    [TestMethod]
    public async Task StoredChat_ComesBackAfterAReload_AndCanBeReopenedOrDeleted()
    {
        // The editor reads the backend's capabilities once per circuit: switch before opening it.
        TestHttpServer.AssistantStoresChats = true;
        var prompt = "stored approval " + InlineBpmn.UniqueSuffix();
        var panel = await AskAsync(prompt);
        await Expect(panel.GetByTestId("editor-ai-chats")).ToBeVisibleAsync();

        // A stored chat's run is its thread and carries only the new turn.
        var sent = SentRun(prompt);
        var chatId = sent.GetProperty("threadId").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(chatId));
        Assert.AreEqual(1, sent.GetProperty("messages").GetArrayLength());

        // The tab keeps the chat's id in localStorage; the turns are read back from the backend.
        await ReloadAfterTheProposalIsSavedAsync();
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

    [TestMethod]
    public async Task ActionsNeedApproval_AndTheirJobsArriveThroughTheEventFeed()
    {
        TestHttpServer.AssistantStoresChats = true;
        TestHttpServer.AssistantAsksForApproval = true;
        var panel = await AskAsync("deploy shop " + InlineBpmn.UniqueSuffix());

        // An activity line, and an approval card whose details are Markdown but never live markup.
        await Expect(panel.GetByTestId("editor-ai-activity")).ToContainTextAsync("plan_deploy");
        var card = panel.GetByTestId("editor-ai-approval");
        await Expect(card.GetByTestId("editor-ai-approval-summary")).ToHaveTextAsync(TestHttpServer.AssistantApprovalSummary);
        await Expect(card.GetByTestId("editor-ai-approval-details").Locator("strong")).ToHaveTextAsync("tag");
        await Expect(card.GetByTestId("editor-ai-approval-details").Locator("script")).ToHaveCountAsync(0);

        // Cancel rejects it: an ordinary run resuming the interrupt, with an empty text.
        await card.GetByTestId("editor-ai-approval-cancel").ClickAsync();
        await Expect(card).ToHaveCountAsync(0);
        await Expect(panel.GetByTestId("editor-ai-message-assistant").Last).ToContainTextAsync(TestHttpServer.AssistantRejectedText);
        var rejection = LastChatRequest();
        Assert.IsFalse(rejection.GetProperty("resume")[0].GetProperty("payload").GetProperty("approved").GetBoolean());
        Assert.AreEqual("", rejection.GetProperty("messages")[0].GetProperty("content").GetString());
        var chatId = rejection.GetProperty("threadId").GetString()!;

        // Run approves the next one, and its job gets a card.
        await SendAsync(panel, "deploy it again " + InlineBpmn.UniqueSuffix());
        await card.GetByTestId("editor-ai-approval-run").ClickAsync();
        await Expect(card).ToHaveCountAsync(0);
        await Expect(panel.GetByTestId("editor-ai-message-assistant").Last).ToContainTextAsync(TestHttpServer.AssistantApprovedText);
        Assert.IsTrue(LastChatRequest().GetProperty("resume")[0].GetProperty("payload").GetProperty("approved").GetBoolean());
        var job = panel.GetByTestId("editor-ai-job");
        await Expect(job).ToHaveAttributeAsync("data-job-status", "queued");

        // The feed updates the card and resumes where it stopped.
        var jobId = TestHttpServer.AssistantLastJobId!;
        TestHttpServer.AssistantPublish(chatId, TestHttpServer.AgUiJob(jobId, "running", "Rolling out"));
        await Expect(job).ToHaveAttributeAsync("data-job-status", "running", new() { Timeout = 15_000 });
        await Expect(job).ToContainTextAsync("Rolling out");
        await Expect(job.GetByTestId("editor-ai-job-link")).ToHaveAttributeAsync("href", "https://ci.test/1");

        // Cancelling asks the backend; the outcome arrives through the feed and ends the card's actions.
        await job.GetByTestId("editor-ai-job-cancel").ClickAsync();
        await Expect(job).ToHaveAttributeAsync("data-job-status", "cancelled", new() { Timeout = 15_000 });
        await Expect(job.GetByTestId("editor-ai-job-cancel")).ToHaveCountAsync(0);
        CollectionAssert.Contains(TestHttpServer.AssistantFeedPositions.ToList(), "1", "the feed resumes with Last-Event-ID");

        // After a reload the feed is read from the start, so what happened while away comes back.
        TestHttpServer.AssistantPublish(chatId,
            TestHttpServer.AgUiApproval(Guid.NewGuid().ToString(), "Apply the finished plan", "**diff**", DateTimeOffset.UtcNow.AddMinutes(10)));
        await Page.ReloadAsync();
        await Page.GetByTestId("editor-ai-toggle").ClickAsync();
        panel = Page.GetByTestId("editor-ai-panel");
        await Expect(panel.GetByTestId("editor-ai-job")).ToHaveAttributeAsync("data-job-status", "cancelled", new() { Timeout = 15_000 });
        await Expect(panel.GetByTestId("editor-ai-approval-summary")).ToHaveTextAsync("Apply the finished plan");

        // A chat can be renamed from the list.
        await panel.GetByTestId("editor-ai-chat-list-toggle").ClickAsync();
        var row = panel.Locator($"[data-testid='editor-ai-chat-row']:has([data-conversation-id='{chatId}'])");
        await row.GetByTestId("editor-ai-chat-rename").ClickAsync();
        await panel.GetByTestId("editor-ai-rename-input").Locator("input").FillAsync("Shop deploy");
        await panel.GetByTestId("editor-ai-rename-save").ClickAsync();
        await Expect(panel.Locator($"[data-conversation-id='{chatId}']")).ToContainTextAsync("Shop deploy");
    }

    private static JsonElement SentRun(string prompt) =>
        TestHttpServer.AssistantRequests.Select(r => JsonDocument.Parse(r.Body).RootElement)
            .Single(r => r.GetProperty("messages")[0].GetProperty("content").GetString() == prompt);

    private static JsonElement LastChatRequest() =>
        JsonDocument.Parse(TestHttpServer.AssistantRequests[^1].Body).RootElement;
}
