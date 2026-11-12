using System.Net;
using System.Text.Json;
using Fleans.Assistant.Contracts;
using Fleans.Web.Services;

namespace Fleans.Web.Tests;

[TestClass]
public class HttpAssistantClientTests
{
    private static AssistantRequest Request() => new(
        [new AssistantMessage(AssistantRoles.User, "approval process")],
        CurrentXml: null,
        Plugins: [new AssistantPlugin("rest-call", "REST call", null)],
        EngineVersion: "1.2.3");

    [TestMethod]
    public async Task StreamsTextAndReturnsTheProposal()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(
            AgUi.Text("Here is "),
            AgUi.Text("a draft."),
            AgUi.Proposal("<definitions/>"));
        var client = backend.CreateClient();
        var deltas = new List<string>();

        // Act
        var reply = await client.SendAsync(Request(), d => { deltas.Add(d); return Task.CompletedTask; }, CancellationToken.None);

        // Assert
        Assert.IsNull(reply.Error);
        Assert.AreEqual("Here is a draft.", reply.Text);
        Assert.AreEqual("<definitions/>", reply.ProposalXml);
        CollectionAssert.AreEqual(new[] { "Here is ", "a draft." }, deltas);
    }

    [TestMethod]
    public async Task PostsAnAgUiRunToTheAgentPath()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(AgUi.Text("ok"));
        var client = backend.CreateClient();

        // Act
        await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        var (request, body) = backend.Calls.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("http://assistant.test/base/assistant/agent", request.RequestUri!.ToString());
        Assert.IsTrue(request.Headers.Accept.Any(a => a.MediaType == "text/event-stream"));
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.IsFalse(string.IsNullOrEmpty(root.GetProperty("threadId").GetString()));
        Assert.IsFalse(string.IsNullOrEmpty(root.GetProperty("runId").GetString()));
        Assert.AreEqual("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.AreEqual("approval process", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.AreEqual(AssistantAgUi.ProposeDiagramTool, root.GetProperty("tools")[0].GetProperty("name").GetString());
        var context = root.GetProperty("context").EnumerateArray().ToDictionary(c => c.GetProperty("description").GetString()!, c => c.GetProperty("value").GetString());
        Assert.AreEqual("1.2.3", context[AssistantAgUi.EngineVersionContext]);
        StringAssert.Contains(context[AssistantAgUi.PluginsContext], "\"taskType\":\"rest-call\"");
        Assert.IsFalse(root.GetProperty("state").TryGetProperty(AssistantAgUi.BpmnXmlProperty, out _), "an empty diagram sends no XML");
    }

    [TestMethod]
    public async Task ErrorEvent_EndsTheReply_KeepingTheTextSoFar()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(
            AgUi.Text("Working"),
            AgUi.Error("model_overloaded", "Try later"),
            AgUi.Proposal("<never/>"));
        var client = backend.CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(new AssistantError("model_overloaded", "Try later"), reply.Error);
        Assert.AreEqual("Working", reply.Text);
        Assert.IsNull(reply.ProposalXml);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, AssistantErrorCodes.Unauthorized)]
    [DataRow(HttpStatusCode.TooManyRequests, AssistantErrorCodes.RateLimited)]
    [DataRow(HttpStatusCode.BadGateway, AssistantErrorCodes.Unavailable)]
    [DataRow(HttpStatusCode.BadRequest, AssistantErrorCodes.BadResponse)]
    public async Task NonSuccessStatus_BecomesAnError(HttpStatusCode status, string expectedCode)
    {
        // Arrange
        var client = new StubAssistantBackend().RespondStatus(status).CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(expectedCode, reply.Error?.Code);
        Assert.AreEqual($"The assistant answered HTTP {(int)status}.", reply.Error?.Message);
    }

    [TestMethod]
    public async Task NonSuccessStatus_UsesTheBackendsMessage()
    {
        // Arrange
        var client = new StubAssistantBackend()
            .RespondStatus(HttpStatusCode.TooManyRequests, """{"code":"quota","message":"Daily quota used up"}""")
            .CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(new AssistantError(AssistantErrorCodes.RateLimited, "Daily quota used up"), reply.Error);
    }

    [TestMethod]
    public async Task UnknownEvents_AreSkipped_AndOnlyTheFirstProposalCounts()
    {
        // Arrange
        var client = new StubAssistantBackend().RespondEvents(
            """{"type":"THINKING_HARDER","anything":1}""",
            AgUi.ToolStarted("t1", "search_docs"),
            AgUi.ToolResult("t1"),
            AgUi.Proposal("<first/>"),
            AgUi.Proposal("<second/>"))
            .CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.IsNull(reply.Error);
        Assert.AreEqual("<first/>", reply.ProposalXml);
    }

    [TestMethod]
    public async Task MalformedEventData_IsABadResponse()
    {
        // Arrange
        var client = new StubAssistantBackend().RespondEvents("not json").CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(AssistantErrorCodes.BadResponse, reply.Error?.Code);
    }

    [TestMethod]
    public async Task UnreachableBackend_IsUnavailable()
    {
        // Arrange
        var client = new StubAssistantBackend()
            .Respond(() => throw new HttpRequestException("connection refused"))
            .CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(AssistantErrorCodes.Unavailable, reply.Error?.Code);
    }

    [TestMethod]
    public async Task StreamThatNeverEnds_TimesOut()
    {
        // Arrange
        var client = new StubAssistantBackend()
            .Respond(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) })
            .CreateClient(timeout: TimeSpan.FromMilliseconds(200));

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(AssistantErrorCodes.Timeout, reply.Error?.Code);
    }

    [TestMethod]
    public async Task CallerCancellation_IsNotSwallowed()
    {
        // Arrange
        var client = new StubAssistantBackend()
            .Respond(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) })
            .CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act + Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.SendAsync(Request(), _ => Task.CompletedTask, cts.Token), "the user's Stop must reach the panel");
    }

    [TestMethod]
    public async Task Capabilities_NotFound_IsABackendWithoutFeatures()
    {
        // Arrange
        var client = new StubAssistantBackend().CreateClient();

        // Act
        var capabilities = await client.GetCapabilitiesAsync(CancellationToken.None);

        // Assert
        Assert.IsNotNull(capabilities, "a 404 is an answer: the backend only serves the chat call");
        Assert.IsEmpty(capabilities.Features);
    }

    [TestMethod]
    public async Task Capabilities_ServerError_IsUnknown()
    {
        // Arrange
        var backend = new StubAssistantBackend { Capabilities = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };

        // Act
        var capabilities = await backend.CreateClient().GetCapabilitiesAsync(CancellationToken.None);

        // Assert
        Assert.IsNull(capabilities, "an unavailable backend is asked again later");
    }

    [TestMethod]
    public async Task Capabilities_AreRead()
    {
        // Arrange
        var backend = new StubAssistantBackend().StoringChats();

        // Act
        var capabilities = await backend.CreateClient().GetCapabilitiesAsync(CancellationToken.None);

        // Assert
        Assert.AreEqual(2, capabilities!.ContractVersion);
        CollectionAssert.Contains(capabilities.Features, AssistantProtocol.PersistenceFeature);
    }

    [TestMethod]
    public async Task StoredChatCalls_UseTheConversationsPath()
    {
        // Arrange
        var backend = new StubAssistantBackend()
            .RespondJson("""{"conversationId":"c1","title":null}""", HttpStatusCode.Created)
            .RespondJson("""{"items":[{"conversationId":"c1","title":"Approval","createdAt":"2026-11-05T10:00:00+00:00","updatedAt":"2026-11-05T10:05:00+00:00"}],"nextCursor":null}""")
            .RespondJson("""{"conversation":{"conversationId":"c1","title":"Approval","createdAt":"2026-11-05T10:00:00+00:00","updatedAt":"2026-11-05T10:05:00+00:00"},"messages":[{"seq":1,"role":"user","text":"hi","bpmnXml":null,"createdAt":"2026-11-05T10:00:00+00:00"}]}""")
            .RespondStatus(HttpStatusCode.NoContent);
        var client = backend.CreateClient();

        // Act
        var (created, _) = await client.CreateConversationAsync(CancellationToken.None);
        var (page, _) = await client.ListConversationsAsync(CancellationToken.None);
        var (history, _) = await client.GetConversationAsync("c1", CancellationToken.None);
        var deleteError = await client.DeleteConversationAsync("c1", CancellationToken.None);

        // Assert
        Assert.AreEqual("c1", created?.ConversationId);
        Assert.AreEqual("Approval", page!.Items.Single().Title);
        Assert.AreEqual("hi", history!.Messages.Single().Text);
        Assert.IsNull(deleteError);
        var calls = backend.Calls.Select(c => $"{c.Request.Method} {c.Request.RequestUri!.AbsolutePath}").ToList();
        CollectionAssert.AreEqual(new[]
        {
            "POST /base/assistant/conversations",
            "GET /base/assistant/conversations",
            "GET /base/assistant/conversations/c1",
            "DELETE /base/assistant/conversations/c1",
        }, calls);
    }

    [TestMethod]
    public async Task StoredChat_ThatIsGone_IsConversationNotFound()
    {
        // Arrange
        var backend = new StubAssistantBackend()
            .RespondStatus(HttpStatusCode.NotFound, """{"code":"conversation_not_found","message":"No such chat."}""")
            .RespondStatus(HttpStatusCode.NotFound);
        var client = backend.CreateClient();

        // Act
        var (_, gone) = await client.GetConversationAsync("c1", CancellationToken.None);
        var reply = await client.SendAsync(Request() with { ConversationId = "c1" }, _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(new AssistantError(AssistantErrorCodes.ConversationNotFound, "No such chat."), gone);
        Assert.AreEqual(AssistantErrorCodes.BadResponse, reply.Error?.Code, "a bare 404 is not a known chat that is gone");
    }

    [TestMethod]
    public async Task RunStarted_IsSkipped_AndTheChatIsTheThread()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(
            AgUi.RunStarted("c1"),
            AgUi.Text("ok"));

        // Act
        var reply = await backend.CreateClient().SendAsync(Request() with { ConversationId = "c1" }, _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual("ok", reply.Text);
        using var json = JsonDocument.Parse(backend.Calls.Single().Body);
        Assert.AreEqual("c1", json.RootElement.GetProperty("threadId").GetString());
    }

    [TestMethod]
    public async Task TheDocumentedMinimalBackendsStream_IsRead()
    {
        // Arrange: the events of the website's minimal ASP.NET Core backend, serialized with AGUIJsonSerializerContext.
        var client = new StubAssistantBackend().RespondEvents(
            new AGUI.Abstractions.RunStartedEvent { ThreadId = "t1", RunId = "r1" },
            new AGUI.Abstractions.TextMessageStartEvent { MessageId = "m1", Role = AGUI.Abstractions.AGUIRoles.Assistant },
            new AGUI.Abstractions.TextMessageContentEvent { MessageId = "m1", Delta = "Here is a draft." },
            new AGUI.Abstractions.TextMessageEndEvent { MessageId = "m1" },
            AssistantAgUi.ProposalEvents("<definitions/>"),
            new AGUI.Abstractions.RunFinishedEvent { ThreadId = "t1", RunId = "r1", Outcome = new AGUI.Abstractions.RunFinishedSuccessOutcome() })
            .CreateClient();
        var progress = new List<AssistantProgress>();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None,
            p => { progress.Add(p); return Task.CompletedTask; });

        // Assert
        Assert.IsNull(reply.Error);
        Assert.AreEqual("Here is a draft.", reply.Text);
        Assert.AreEqual("<definitions/>", reply.ProposalXml);
        Assert.IsEmpty(progress, "the proposal tool call is not an activity line");
    }

    [TestMethod]
    public async Task ProposalArguments_MayArriveInChunks()
    {
        // Arrange
        var client = new StubAssistantBackend().RespondEvents(
            new AGUI.Abstractions.ToolCallStartEvent { ToolCallId = "p1", ToolCallName = AssistantAgUi.ProposeDiagramTool },
            new AGUI.Abstractions.ToolCallArgsEvent { ToolCallId = "p1", Delta = "{\"bpmnXml\":\"<defin" },
            new AGUI.Abstractions.ToolCallArgsEvent { ToolCallId = "p1", Delta = "itions/>\"}" },
            new AGUI.Abstractions.ToolCallEndEvent { ToolCallId = "p1" })
            .CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.IsNull(reply.Error);
        Assert.AreEqual("<definitions/>", reply.ProposalXml);
    }

    [TestMethod]
    public async Task ProposalWithoutXml_IsABadResponse()
    {
        // Arrange
        var client = new StubAssistantBackend().RespondEvents(
            new AGUI.Abstractions.ToolCallStartEvent { ToolCallId = "p1", ToolCallName = AssistantAgUi.ProposeDiagramTool },
            new AGUI.Abstractions.ToolCallArgsEvent { ToolCallId = "p1", Delta = "{}" },
            new AGUI.Abstractions.ToolCallEndEvent { ToolCallId = "p1" })
            .CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(AssistantErrorCodes.BadResponse, reply.Error?.Code);
    }

    [TestMethod]
    public async Task RunErrorWithoutCode_IsABadResponseWithTheBackendsMessage()
    {
        // Arrange
        var client = new StubAssistantBackend().RespondEvents(new AGUI.Abstractions.RunErrorEvent { Message = "Model failed" }).CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual(new AssistantError(AssistantErrorCodes.BadResponse, "Model failed"), reply.Error);
    }

    [TestMethod]
    public async Task TextChunks_AreStreamedToo()
    {
        // Arrange
        var client = new StubAssistantBackend().RespondEvents(
            new AGUI.Abstractions.TextMessageChunkEvent { MessageId = "m1", Role = "assistant", Delta = "Hi" })
            .CreateClient();

        // Act
        var reply = await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.AreEqual("Hi", reply.Text);
    }

    private sealed class HangingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
