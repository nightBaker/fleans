using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using Fleans.Application.CustomTasks;
using Fleans.Assistant.Contracts;
using Fleans.Infrastructure.Bpmn;
using Fleans.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Fleans.Web.Tests;

/// <summary>Contract v2's actions (#1017) over AG-UI (#1054): activity lines, approvals, jobs and the chat's event feed.</summary>
[TestClass]
public class AssistantActionsTests
{
    private const string ChatId = "c1";

    private static EditorAssistant CreateAssistant(StubAssistantBackend backend)
    {
        var catalog = Substitute.For<ICustomTaskCatalogGrain>();
        catalog.GetAll().Returns(new List<CustomTaskCatalogEntry>());
        var grains = Substitute.For<IGrainFactory>();
        grains.GetGrain<ICustomTaskCatalogGrain>(0).Returns(catalog);
        return new EditorAssistant(backend.CreateClient(), new BpmnConverter(NullLogger<BpmnConverter>.Instance),
            grains, NullLogger<EditorAssistant>.Instance) { EventsReconnectDelay = TimeSpan.Zero };
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, AssistantProtocol.JsonOptions);

    private static AssistantApprovalRequest Approval(string id = "a1", DateTimeOffset? expiresAt = null) =>
        new(id, "deploy.apply", "Deploy shop to stage", "**tag** 1.2", expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10));

    private static BaseEvent ApprovalEvent(AssistantApprovalRequest approval) => AgUi.Approvals(approval);

    private static AssistantJobUpdate Job(string status, string id = "j1") => new(id, "deploy.apply", status, "Deploying", "https://ci.test/1");

    private static BaseEvent JobEvent(AssistantJobUpdate job) => AgUi.Job(job);

    // A backend tool call: TOOL_CALL_START is the started line, TOOL_CALL_RESULT the completed one.
    private static BaseEvent ActivityEvent(string id, string status) =>
        status == AssistantActivityStatus.Started ? AgUi.ToolStarted(id, "plan_deploy") : AgUi.ToolResult(id);

    private static StubAssistantBackend StoringBackend() => new StubAssistantBackend().StoringChats();

    private static Task<string?> RunAsync(EditorAssistant assistant, AssistantChat chat, string text = "deploy shop") =>
        assistant.RunTurnAsync(chat, text, currentXml: null, _ => Task.CompletedTask, () => Task.CompletedTask, CancellationToken.None);

    private static Task DecideAsync(EditorAssistant assistant, AssistantChat chat, AssistantApprovalRequest approval, bool approved) =>
        assistant.DecideAsync(chat, approval, approved, currentXml: "<xml />", _ => Task.CompletedTask, () => Task.CompletedTask, CancellationToken.None);

    private static AssistantRequest Sent(StubAssistantBackend backend, int call = 0) =>
        backend.SentRequest(call);

    // ---- The chat stream

    [TestMethod]
    public async Task Turn_CollectsActivityApprovalsAndJobs_AndLeavesTheTextAlone()
    {
        // Arrange
        var backend = StoringBackend().RespondEvents(
            ActivityEvent("t1", AssistantActivityStatus.Started),
            AgUi.Text("Planning."),
            ActivityEvent("t1", AssistantActivityStatus.Completed),
            JobEvent(Job(AssistantJobStatus.Queued)),
            ApprovalEvent(Approval()));
        var chat = new AssistantChat { ConversationId = ChatId };
        var progress = 0;

        // Act
        await CreateAssistant(backend).RunTurnAsync(chat, "deploy shop", null, _ => Task.CompletedTask, () => Task.CompletedTask,
            CancellationToken.None, onProgress: () => { progress++; return Task.CompletedTask; });

        // Assert
        Assert.AreEqual(4, progress);
        var activity = chat.Activities.Single();
        Assert.AreEqual(AssistantActivityStatus.Completed, activity.Status, "the same id updates one line");
        Assert.AreEqual("plan_deploy", activity.Tool, "the tool call's name is kept for its result");
        Assert.AreEqual("a1", chat.Approvals.Single().ActionId);
        Assert.AreEqual(AssistantJobStatus.Queued, chat.Jobs.Single().Status);
        Assert.AreEqual("Planning.", chat.Entries[^1].Text);
        Assert.IsNull(chat.LastError);
    }

    [TestMethod]
    public async Task NextTurn_ClearsActivity_ButKeepsApprovalsAndJobs()
    {
        // Arrange
        var backend = StoringBackend()
            .RespondEvents(ActivityEvent("t1", AssistantActivityStatus.Completed), JobEvent(Job(AssistantJobStatus.Running)), ApprovalEvent(Approval()))
            .RespondEvents(AgUi.Text("ok"));
        var chat = new AssistantChat { ConversationId = ChatId };
        var assistant = CreateAssistant(backend);
        await RunAsync(assistant, chat);

        // Act
        await RunAsync(assistant, chat, "and then?");

        // Assert
        Assert.IsEmpty(chat.Activities);
        Assert.HasCount(1, chat.Approvals);
        Assert.HasCount(1, chat.Jobs);
    }

    [TestMethod]
    public async Task ExpiredApproval_IsNotOffered()
    {
        // Arrange
        var backend = StoringBackend().RespondEvents(ApprovalEvent(Approval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1))));
        var chat = new AssistantChat { ConversationId = ChatId };

        // Act
        await RunAsync(CreateAssistant(backend), chat);

        // Assert
        Assert.IsEmpty(chat.Approvals);
    }

    [TestMethod]
    public void FinishedJob_IsNotOverwrittenByAnOlderUpdate()
    {
        // Arrange: the chat stream and the event feed both report a job, in any order.
        var chat = new AssistantChat();
        chat.Apply(new AssistantProgress(Job: Job(AssistantJobStatus.Succeeded)));

        // Act
        chat.Apply(new AssistantProgress(Job: Job(AssistantJobStatus.Queued)));

        // Assert
        Assert.AreEqual(AssistantJobStatus.Succeeded, chat.Jobs.Single().Status);
    }

    [TestMethod]
    public void Update_WithoutItsId_IsIgnored()
    {
        // Arrange: a backend bug must not break the panel.
        var chat = new AssistantChat();
        var noJobId = JsonSerializer.Deserialize<AssistantJobUpdate>("""{"status":"queued"}""", AssistantProtocol.JsonOptions)!;
        var noActionId = JsonSerializer.Deserialize<AssistantApprovalRequest>("""{"summary":"x"}""", AssistantProtocol.JsonOptions)!;
        var noActivityId = JsonSerializer.Deserialize<AssistantActivity>("""{"status":"started"}""", AssistantProtocol.JsonOptions)!;

        // Act
        chat.Apply(new AssistantProgress(Job: noJobId));
        chat.Apply(new AssistantProgress(Approval: noActionId));
        chat.Apply(new AssistantProgress(Activity: noActivityId));

        // Assert
        Assert.IsEmpty(chat.Jobs);
        Assert.IsEmpty(chat.Approvals);
        Assert.IsEmpty(chat.Activities);
    }

    [TestMethod]
    public async Task ChatThatIsGone_DropsItsCards()
    {
        // Arrange
        var backend = StoringBackend()
            .RespondEvents(JobEvent(Job(AssistantJobStatus.Running)), ApprovalEvent(Approval()))
            .RespondStatus(HttpStatusCode.NotFound, """{"code":"conversation_not_found","message":"gone"}""");
        var chat = new AssistantChat { ConversationId = ChatId };
        var assistant = CreateAssistant(backend);
        await RunAsync(assistant, chat);

        // Act
        await RunAsync(assistant, chat, "still there?");

        // Assert
        Assert.IsNull(chat.ConversationId);
        Assert.IsEmpty(chat.Approvals);
        Assert.IsEmpty(chat.Jobs);
    }

    [TestMethod]
    public void ResetChat_DropsItsCardsAndFeedPosition()
    {
        // Arrange
        var chat = new AssistantChat { ConversationId = ChatId, LastEventId = "7" };
        chat.Apply(new AssistantProgress(Approval: Approval()));
        chat.Apply(new AssistantProgress(Job: Job(AssistantJobStatus.Running)));
        chat.Apply(new AssistantProgress(Activity: new AssistantActivity("t1", "A", "T", AssistantActivityStatus.Started, "s")));

        // Act
        chat.Reset("c2");

        // Assert
        Assert.IsEmpty(chat.Approvals);
        Assert.IsEmpty(chat.Jobs);
        Assert.IsEmpty(chat.Activities);
        Assert.IsNull(chat.LastEventId);
    }

    // ---- Approvals

    [TestMethod]
    public async Task Approve_SendsAnOrdinaryChatRequestWithTheDecisionAndAnEmptyText()
    {
        // Arrange
        var backend = StoringBackend().RespondEvents(
            ActivityEvent("approval-a1", AssistantActivityStatus.Completed),
            JobEvent(Job(AssistantJobStatus.Queued)),
            AgUi.Text("Deploy queued."));
        var chat = new AssistantChat { ConversationId = ChatId };
        chat.Add(new(AssistantRoles.User, "deploy shop"));
        var approval = Approval();
        chat.Apply(new AssistantProgress(Approval: approval));

        // Act
        await DecideAsync(CreateAssistant(backend), chat, approval, approved: true);

        // Assert
        Assert.HasCount(1, backend.Calls);
        StringAssert.EndsWith(backend.Calls[0].Request.RequestUri!.AbsolutePath, "/assistant/agent");
        var sent = Sent(backend);
        Assert.AreEqual(ChatId, sent.ConversationId);
        Assert.AreEqual("<xml />", sent.CurrentXml, "the editor's diagram is sent on every turn");
        var message = sent.Messages.Single();
        Assert.AreEqual(AssistantRoles.User, message.Role);
        Assert.AreEqual("", message.Text);
        var decision = sent.Approvals!.Single();
        Assert.AreEqual("a1", decision.ActionId);
        Assert.IsTrue(decision.Approved);

        Assert.IsEmpty(chat.Approvals, "the card is answered");
        Assert.AreEqual(AssistantJobStatus.Queued, chat.Jobs.Single().Status);
        Assert.HasCount(2, chat.Entries, "no empty user message joins the chat");
        Assert.AreEqual("Deploy queued.", chat.Entries[^1].Text);
    }

    [TestMethod]
    public async Task Cancel_SendsARejection()
    {
        // Arrange
        var backend = StoringBackend().RespondEvents(AgUi.Text("Rejected: nothing was changed."));
        var chat = new AssistantChat { ConversationId = ChatId };
        var approval = Approval();
        chat.Apply(new AssistantProgress(Approval: approval));

        // Act
        await DecideAsync(CreateAssistant(backend), chat, approval, approved: false);

        // Assert
        Assert.IsFalse(Sent(backend).Approvals!.Single().Approved);
        Assert.IsEmpty(chat.Approvals);
    }

    [TestMethod]
    [DataRow(AssistantErrorCodes.ApprovalInvalid)]
    [DataRow(AssistantErrorCodes.ApprovalExpired)]
    [DataRow(AssistantErrorCodes.ForbiddenAction)]
    public async Task RefusedDecision_ShowsTheErrorAndDropsTheCard(string code)
    {
        // Arrange
        var backend = StoringBackend().RespondEvents(AgUi.Error(code, "That action was already decided."));
        var chat = new AssistantChat { ConversationId = ChatId };
        var approval = Approval();
        chat.Apply(new AssistantProgress(Approval: approval));

        // Act
        await DecideAsync(CreateAssistant(backend), chat, approval, approved: true);

        // Assert
        Assert.AreEqual(code, chat.LastError!.Code);
        Assert.IsEmpty(chat.Approvals);
    }

    [TestMethod]
    public async Task UnreachableBackend_KeepsTheCardToTryAgain()
    {
        // Arrange
        var backend = StoringBackend().RespondStatus(HttpStatusCode.ServiceUnavailable);
        var chat = new AssistantChat { ConversationId = ChatId };
        var approval = Approval();
        chat.Apply(new AssistantProgress(Approval: approval));

        // Act
        await DecideAsync(CreateAssistant(backend), chat, approval, approved: true);

        // Assert
        Assert.AreEqual(AssistantErrorCodes.Unavailable, chat.LastError!.Code);
        Assert.HasCount(1, chat.Approvals);
    }

    [TestMethod]
    public async Task ClickAfterExpiry_CallsNothingAndSaysSo()
    {
        // Arrange
        var backend = StoringBackend();
        var chat = new AssistantChat { ConversationId = ChatId };
        var approval = Approval(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));
        chat.Apply(new AssistantProgress(Approval: approval));

        // Act
        await DecideAsync(CreateAssistant(backend), chat, approval, approved: true);

        // Assert
        Assert.IsEmpty(backend.Calls);
        Assert.AreEqual(AssistantErrorCodes.ApprovalExpired, chat.LastError!.Code);
        Assert.IsEmpty(chat.Approvals);
    }

    [TestMethod]
    public async Task DecidedApproval_DoesNotComeBackWhenTheFeedRepeatsIt()
    {
        // Arrange: reopening a chat in the same session reads its events from the start.
        var approval = Approval();
        var backend = StoringBackend()
            .RespondEvents(AgUi.Text("Done."))
            .RespondFeed(("1", ApprovalEvent(approval)))
            .RespondStatus(HttpStatusCode.NotFound);
        var chat = new AssistantChat { ConversationId = ChatId };
        chat.Apply(new AssistantProgress(Approval: approval));
        var assistant = CreateAssistant(backend);
        await DecideAsync(assistant, chat, approval, approved: true);
        chat.Reset(ChatId);

        // Act
        await assistant.WatchChatAsync(chat, ChatId, () => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.IsEmpty(chat.Approvals);
    }

    // ---- The event feed

    [TestMethod]
    public async Task Feed_AppliesEvents_ReconnectsFromTheLastId_AndStopsOnNotFound()
    {
        // Arrange
        var backend = StoringBackend()
            .RespondFeed(
                ("5", JobEvent(Job(AssistantJobStatus.Running))),
                ("6", """{"type":"SOMETHING_NEW","x":1}"""))
            .RespondFeed(
                ("7", JobEvent(Job(AssistantJobStatus.Succeeded))),
                ("8", ApprovalEvent(Approval())))
            .RespondStatus(HttpStatusCode.NotFound);
        var chat = new AssistantChat { ConversationId = ChatId };
        var changes = 0;

        // Act
        await CreateAssistant(backend).WatchChatAsync(chat, ChatId, () => { changes++; return Task.CompletedTask; }, CancellationToken.None);

        // Assert
        Assert.HasCount(3, backend.Calls);
        StringAssert.EndsWith(backend.Calls[0].Request.RequestUri!.AbsolutePath, "/assistant/conversations/c1/events");
        Assert.AreEqual(HttpMethod.Get, backend.Calls[0].Request.Method);
        Assert.IsFalse(backend.Calls[0].Request.Headers.Contains("Last-Event-ID"), "the first connection reads from the start");
        Assert.AreEqual("6", backend.Calls[1].Request.Headers.GetValues("Last-Event-ID").Single(), "an unknown event still moves the position");
        Assert.AreEqual("8", backend.Calls[2].Request.Headers.GetValues("Last-Event-ID").Single());
        Assert.AreEqual(3, changes, "unknown events change nothing");
        Assert.AreEqual(AssistantJobStatus.Succeeded, chat.Jobs.Single().Status);
        Assert.HasCount(1, chat.Approvals);
    }

    [TestMethod]
    public async Task Feed_SkipsAnUnreadableEvent_InsteadOfRepeatingItForever()
    {
        // Arrange
        var backend = StoringBackend()
            .RespondFeed(("1", "{not json"), ("2", JobEvent(Job(AssistantJobStatus.Running))))
            .RespondStatus(HttpStatusCode.NotFound);
        var chat = new AssistantChat { ConversationId = ChatId };

        // Act
        await CreateAssistant(backend).WatchChatAsync(chat, ChatId, () => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(AssistantJobStatus.Running, chat.Jobs.Single().Status);
        Assert.AreEqual("2", backend.Calls[1].Request.Headers.GetValues("Last-Event-ID").Single());
    }

    [TestMethod]
    public async Task Feed_RetriesAnOutage_AndStopsOnARejectedToken()
    {
        // Arrange
        var backend = StoringBackend()
            .RespondStatus(HttpStatusCode.ServiceUnavailable)
            .RespondFeed(("1", JobEvent(Job(AssistantJobStatus.Running))))
            .RespondStatus(HttpStatusCode.Unauthorized);
        var chat = new AssistantChat { ConversationId = ChatId };

        // Act
        await CreateAssistant(backend).WatchChatAsync(chat, ChatId, () => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.HasCount(3, backend.Calls);
        Assert.HasCount(1, chat.Jobs);
        Assert.IsNull(chat.LastError, "the feed is in the background; the next message reports a rejected token");
    }

    [TestMethod]
    public async Task Feed_EndsQuietly_WhenCancelled()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var backend = StoringBackend().RespondFeed(("1", JobEvent(Job(AssistantJobStatus.Running))));
        var chat = new AssistantChat { ConversationId = ChatId };

        // Act
        await CreateAssistant(backend).WatchChatAsync(chat, ChatId, async () => { await cts.CancelAsync(); }, cts.Token);

        // Assert: the only queued answer was used, and no second call was made.
        Assert.HasCount(1, backend.Calls);
    }

    [TestMethod]
    public async Task Feed_StopsApplying_WhenTheChatHasMovedOn()
    {
        // Arrange: the user switched chats while the event was on its way.
        var backend = StoringBackend().RespondFeed(("1", JobEvent(Job(AssistantJobStatus.Running))));
        var chat = new AssistantChat { ConversationId = "other" };

        // Act
        await CreateAssistant(backend).WatchChatAsync(chat, ChatId, () => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.IsEmpty(chat.Jobs);
        Assert.IsNull(chat.LastEventId);
    }

    // ---- The other calls

    [TestMethod]
    public async Task CancelJob_PostsToTheJobAndReportsARefusal()
    {
        // Arrange
        var backend = StoringBackend()
            .RespondStatus(HttpStatusCode.Accepted)
            .RespondStatus(HttpStatusCode.NotFound, """{"code":"job_not_found","message":"This job has finished."}""");
        var assistant = CreateAssistant(backend);

        // Act
        var accepted = await assistant.CancelJobAsync("j/1", CancellationToken.None);
        var refused = await assistant.CancelJobAsync("j2", CancellationToken.None);

        // Assert
        Assert.IsNull(accepted);
        Assert.AreEqual(HttpMethod.Post, backend.Calls[0].Request.Method);
        Assert.AreEqual("/base/assistant/jobs/j%2F1/cancel", backend.Calls[0].Request.RequestUri!.AbsolutePath);
        Assert.AreEqual("This job has finished.", refused!.Message);
    }

    [TestMethod]
    public async Task RenameChat_PatchesTheTitle()
    {
        // Arrange
        var backend = StoringBackend().RespondJson("""{"conversationId":"c1","title":"Shop deploy","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z"}""");

        // Act
        var error = await CreateAssistant(backend).RenameChatAsync(ChatId, "Shop deploy", CancellationToken.None);

        // Assert
        Assert.IsNull(error);
        Assert.AreEqual(HttpMethod.Patch, backend.Calls[0].Request.Method);
        StringAssert.EndsWith(backend.Calls[0].Request.RequestUri!.AbsolutePath, "/assistant/conversations/c1");
        Assert.AreEqual("Shop deploy", JsonSerializer.Deserialize<AssistantConversationUpdate>(backend.Calls[0].Body, AssistantProtocol.JsonOptions)!.Title);
    }

    [TestMethod]
    public async Task ListChats_PassesTheCursorForTheNextPage()
    {
        // Arrange
        var backend = StoringBackend()
            .RespondJson("""{"items":[],"nextCursor":"n/2"}""")
            .RespondJson("""{"items":[],"nextCursor":null}""");
        var assistant = CreateAssistant(backend);

        // Act
        var (first, _) = await assistant.ListChatsAsync(CancellationToken.None);
        await assistant.ListChatsAsync(CancellationToken.None, first!.NextCursor);

        // Assert
        Assert.AreEqual("", backend.Calls[0].Request.RequestUri!.Query);
        Assert.AreEqual("?cursor=n%2F2", backend.Calls[1].Request.RequestUri!.Query);
    }

    // ---- The AG-UI mapping (#1054)

    [TestMethod]
    public void Request_SurvivesTheRunAgentInput()
    {
        // Arrange
        var request = new AssistantRequest(
            [new AssistantMessage(AssistantRoles.User, "hi"), new AssistantMessage(AssistantRoles.Assistant, "hello"), new AssistantMessage(AssistantRoles.User, "")],
            "<definitions/>",
            [new AssistantPlugin("rest-call", "REST call", """{"type":"object"}""")],
            "0.12.0",
            ChatId,
            [new AssistantApprovalDecision("a1", true, "go"), new AssistantApprovalDecision("a2", false)]);

        // Act
        var json = JsonSerializer.Serialize(AssistantAgUi.ToRunAgentInput(request), AGUIJsonSerializerContext.Default.RunAgentInput);
        var back = AssistantAgUi.FromRunAgentInput(JsonSerializer.Deserialize(json, AGUIJsonSerializerContext.Default.RunAgentInput)!);

        // Assert
        CollectionAssert.AreEqual(request.Messages, back.Messages);
        Assert.AreEqual(request.CurrentXml, back.CurrentXml);
        CollectionAssert.AreEqual(request.Plugins, back.Plugins);
        Assert.AreEqual(request.EngineVersion, back.EngineVersion);
        Assert.AreEqual(ChatId, back.ConversationId);
        CollectionAssert.AreEqual(request.Approvals, back.Approvals);
        StringAssert.Contains(json, "\"threadId\":\"" + ChatId + "\"");
        StringAssert.Contains(json, "\"" + AssistantAgUi.ProposeDiagramTool + "\"");
    }

    [TestMethod]
    public void StatelessRequest_GetsAFreshThreadAndNoDiagramState()
    {
        // Arrange
        var request = new AssistantRequest([new AssistantMessage(AssistantRoles.User, "hi")], null, [], "0.12.0");

        // Act
        var first = AssistantAgUi.ToRunAgentInput(request);
        var second = AssistantAgUi.ToRunAgentInput(request);

        // Assert
        Assert.AreNotEqual(first.ThreadId, second.ThreadId);
        Assert.IsNull(first.Resume);
        Assert.IsNull(AssistantAgUi.FromRunAgentInput(first).CurrentXml);
    }

    [TestMethod]
    public void Approval_SurvivesTheInterrupt()
    {
        // Arrange
        var approval = Approval(expiresAt: new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));

        // Act
        var json = JsonSerializer.Serialize(AssistantAgUi.ToInterrupt(approval), AGUIJsonSerializerContext.Default.AGUIInterrupt);
        var back = AssistantAgUi.FromInterrupt(JsonSerializer.Deserialize(json, AGUIJsonSerializerContext.Default.AGUIInterrupt)!);

        // Assert
        Assert.AreEqual(approval, back);
    }

    [TestMethod]
    public void InterruptWithoutDetailsOrExpiry_NeverExpires()
    {
        // Act
        var approval = AssistantAgUi.FromInterrupt(new AGUIInterrupt { Id = "i1", Reason = "tool_call" });

        // Assert
        Assert.AreEqual("i1", approval.ActionId);
        Assert.AreEqual("tool_call", approval.Summary, "the reason stands in for a missing message");
        Assert.AreEqual("", approval.DetailsMarkdown);
        Assert.AreEqual(DateTimeOffset.MaxValue, approval.ExpiresAt);
    }

    [TestMethod]
    public void CancelledResume_IsARefusal()
    {
        // Act
        var decision = AssistantAgUi.FromResume(new AGUIResume { InterruptId = "a1", Status = ResumeStatus.Cancelled });

        // Assert
        Assert.IsFalse(decision.Approved);
    }

    [TestMethod]
    [DataRow(AssistantJobStatus.Succeeded, true)]
    [DataRow(AssistantJobStatus.Failed, true)]
    [DataRow(AssistantJobStatus.Cancelled, true)]
    [DataRow(AssistantJobStatus.Queued, false)]
    [DataRow(AssistantJobStatus.Running, false)]
    public void JobStatus_KnowsWhichAreFinal(string status, bool final) =>
        Assert.AreEqual(final, AssistantJobStatus.IsFinal(status));
}
