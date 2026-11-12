using System.Text.Json;
using Fleans.Application.CustomTasks;
using Fleans.Assistant.Contracts;
using Fleans.Infrastructure.Bpmn;
using Fleans.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Fleans.Web.Tests;

[TestClass]
public class EditorAssistantTests
{
    private const string ValidXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="d" targetNamespace="http://bpmn.io/schema/bpmn">
          <process id="approval" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="Approve" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    private const string InvalidXml = "<definitions><process id=\"broken\">";

    private static EditorAssistant CreateAssistant(StubAssistantBackend backend, IReadOnlyList<CustomTaskCatalogEntry>? plugins = null)
    {
        var catalog = Substitute.For<ICustomTaskCatalogGrain>();
        catalog.GetAll().Returns(plugins ?? []);
        var grains = Substitute.For<IGrainFactory>();
        grains.GetGrain<ICustomTaskCatalogGrain>(0).Returns(catalog);
        return new EditorAssistant(backend.CreateClient(), new BpmnConverter(NullLogger<BpmnConverter>.Instance),
            grains, NullLogger<EditorAssistant>.Instance);
    }

    private static IEnumerable<AGUI.Abstractions.BaseEvent> Proposal(string xml) => AgUi.Proposal(xml);

    private static Task<string?> RunAsync(EditorAssistant assistant, AssistantChat conversation, string text = "approval process") =>
        assistant.RunTurnAsync(conversation, text, currentXml: null, _ => Task.CompletedTask, () => Task.CompletedTask, CancellationToken.None);

    [TestMethod]
    public async Task Request_LeavesOutPrivatePluginsTheProcessMayNotUse()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(AgUi.Text("ok"));
        var assistant = CreateAssistant(backend, [
            new CustomTaskCatalogEntry("open", null, null, ["silo-1"]),
            new CustomTaskCatalogEntry("mine", null, null, ["silo-1"], null, CustomTaskVisibility.Private, ["billing"]),
            new CustomTaskCatalogEntry("theirs", null, null, ["silo-1"], null, CustomTaskVisibility.Private, ["shop"])]);

        // Act
        await assistant.RunTurnAsync(new AssistantChat(), "hello", currentXml: null,
            _ => Task.CompletedTask, () => Task.CompletedTask, CancellationToken.None, processKey: "billing");

        // Assert
        var sent = backend.SentRequest(0);
        CollectionAssert.AreEqual(new[] { "open", "mine" }, sent.Plugins.Select(p => p.TaskType).ToList());
    }

    [TestMethod]
    public async Task ValidProposal_IsReturnedAndOffered()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(AgUi.Text("Done."), Proposal(ValidXml));
        var conversation = new AssistantChat();

        // Act
        var proposal = await RunAsync(CreateAssistant(backend), conversation);

        // Assert
        Assert.AreEqual(ValidXml, proposal);
        Assert.IsNull(conversation.LastError);
        Assert.AreEqual(1, backend.Calls.Count);
        var reply = conversation.Entries[^1];
        Assert.AreEqual(AssistantRoles.Assistant, reply.Role);
        Assert.AreEqual("Done.", reply.Text);
        Assert.IsTrue(reply.ProposalValid);
    }

    [TestMethod]
    public async Task InvalidProposal_IsSentBackForAFix_ThenTheFixedOneIsOffered()
    {
        // Arrange
        var backend = new StubAssistantBackend()
            .RespondEvents(Proposal(InvalidXml))
            .RespondEvents(AgUi.Text("Fixed."), Proposal(ValidXml));
        var conversation = new AssistantChat();

        // Act
        var proposal = await RunAsync(CreateAssistant(backend), conversation);

        // Assert
        Assert.AreEqual(ValidXml, proposal);
        Assert.AreEqual(2, backend.Calls.Count);
        var fixRequest = backend.SentRequest(1);
        var last = fixRequest.Messages[^1];
        Assert.AreEqual(AssistantRoles.User, last.Role);
        StringAssert.StartsWith(last.Text, "fix: ");
        StringAssert.Contains(fixRequest.Messages[^2].Text, InvalidXml, "the backend needs the rejected XML to fix it");
        Assert.IsTrue(conversation.Entries.Single(e => e.Automatic).Text.StartsWith("fix: "));
        Assert.IsFalse(conversation.Entries.Single(e => e.ProposalXml == InvalidXml).ProposalValid);
    }

    [TestMethod]
    public async Task StillInvalidAfterTheAutomaticFixes_EndsWithAnError()
    {
        // Arrange
        var backend = new StubAssistantBackend()
            .RespondEvents(Proposal(InvalidXml))
            .RespondEvents(Proposal(InvalidXml))
            .RespondEvents(Proposal(InvalidXml));
        var conversation = new AssistantChat();

        // Act
        var proposal = await RunAsync(CreateAssistant(backend), conversation);

        // Assert
        Assert.IsNull(proposal);
        Assert.AreEqual(1 + EditorAssistant.MaxAutomaticFixes, backend.Calls.Count);
        Assert.AreEqual(AssistantErrorCodes.InvalidProposal, conversation.LastError?.Code);
        Assert.IsFalse(conversation.Entries.Any(e => e.ProposalValid));
        Assert.AreEqual(EditorAssistant.MaxAutomaticFixes, conversation.Entries.Count(e => e.Automatic));
    }

    [TestMethod]
    [DataRow(System.Net.HttpStatusCode.Unauthorized, AssistantErrorCodes.Unauthorized)]
    [DataRow(System.Net.HttpStatusCode.TooManyRequests, AssistantErrorCodes.RateLimited)]
    public async Task HttpErrors_AreSurfacedOnTheConversation(System.Net.HttpStatusCode status, string code)
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondStatus(status);
        var conversation = new AssistantChat();

        // Act
        var proposal = await RunAsync(CreateAssistant(backend), conversation);

        // Assert
        Assert.IsNull(proposal);
        Assert.AreEqual(code, conversation.LastError?.Code);
        Assert.AreEqual(1, conversation.Entries.Count, "only the user's message; no empty assistant reply");
    }

    [TestMethod]
    public async Task ErrorEvent_IsSurfacedWithTheBackendsMessage()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(AgUi.Error("policy", "Not allowed"));
        var conversation = new AssistantChat();

        // Act
        await RunAsync(CreateAssistant(backend), conversation);

        // Assert
        Assert.AreEqual(new AssistantError("policy", "Not allowed"), conversation.LastError);
    }

    [TestMethod]
    public async Task NextTurn_ClearsTheLastError()
    {
        // Arrange
        var backend = new StubAssistantBackend()
            .RespondStatus(System.Net.HttpStatusCode.ServiceUnavailable)
            .RespondEvents(AgUi.Text("Back."));
        var assistant = CreateAssistant(backend);
        var conversation = new AssistantChat();
        await RunAsync(assistant, conversation);

        // Act
        await RunAsync(assistant, conversation, "again");

        // Assert
        Assert.IsNull(conversation.LastError);
        Assert.AreEqual("Back.", conversation.Entries[^1].Text);
    }

    [TestMethod]
    public async Task SendsThePluginCatalogAndEngineVersion()
    {
        // Arrange
        var schema = new CustomTaskParameterSchema([
            new CustomTaskParameterSpec("url", "URL", CustomTaskParameterType.String, true, null, null, null)]);
        var backend = new StubAssistantBackend().RespondEvents(AgUi.Text("ok"));
        var assistant = CreateAssistant(backend, [
            new CustomTaskCatalogEntry("rest-call", "REST call", schema, ["silo-1"]),
            new CustomTaskCatalogEntry("bare", null, null, ["silo-1"])]);

        // Act
        await RunAsync(assistant, new AssistantChat());

        // Assert
        var sent = backend.SentRequest(0);
        Assert.AreEqual(EditorAssistant.EngineVersion, sent.EngineVersion);
        Assert.AreNotEqual("", sent.EngineVersion);
        Assert.AreEqual(2, sent.Plugins.Count);
        Assert.AreEqual("REST call", sent.Plugins[0].DisplayName);
        using var parameters = JsonDocument.Parse(sent.Plugins[0].ParameterSchemaJson!);
        var url = parameters.RootElement.GetProperty("parameters")[0];
        Assert.AreEqual("url", url.GetProperty("name").GetString());
        Assert.AreEqual("String", url.GetProperty("type").GetString());
        Assert.AreEqual("bare", sent.Plugins[1].DisplayName, "falls back to the task type");
        Assert.IsNull(sent.Plugins[1].ParameterSchemaJson);
    }

    [TestMethod]
    public void BuildMessages_SendsOnlyTheLatestProposalInFull()
    {
        // Arrange
        var conversation = new AssistantChat();
        conversation.Add(new(AssistantRoles.User, "first"));
        conversation.Add(new(AssistantRoles.Assistant, "v1", "<v1/>", true));
        conversation.Add(new(AssistantRoles.User, "second"));
        conversation.Add(new(AssistantRoles.Assistant, "", "<v2/>", true));
        conversation.Add(new(AssistantRoles.User, "third"));

        // Act
        var messages = EditorAssistant.BuildMessages(conversation);

        // Assert
        Assert.AreEqual("v1\n\n" + EditorAssistant.ProposalPlaceholder, messages[1].Text);
        Assert.AreEqual("```xml\n<v2/>\n```", messages[3].Text);
        Assert.AreEqual("third", messages[4].Text);
    }

    private static AssistantRequest Sent(StubAssistantBackend backend, int call) =>
        backend.SentRequest(call);

    [TestMethod]
    public async Task BackendWithoutStoredChats_SendsTheWholeConversationInAFreshThread()
    {
        // Arrange
        var backend = new StubAssistantBackend()
            .RespondEvents(AgUi.Text("one"))
            .RespondEvents(AgUi.Text("two"));
        var assistant = CreateAssistant(backend);
        var chat = new AssistantChat();

        // Act
        await RunAsync(assistant, chat, "first");
        await RunAsync(assistant, chat, "second");

        // Assert
        Assert.IsNull(chat.ConversationId);
        Assert.AreNotEqual(backend.SentRun(0).ThreadId, backend.SentRun(1).ThreadId, "no stored chat: each run is its own thread");
        Assert.HasCount(3, Sent(backend, 1).Messages, "the stateless run carries the history");
        Assert.AreEqual(1, backend.CapabilitiesCalls, "a 404 is remembered for the circuit");
    }

    [TestMethod]
    public async Task StoredChat_IsCreatedOnce_AndEachRequestCarriesOnlyTheNewTurn()
    {
        // Arrange
        var backend = new StubAssistantBackend().StoringChats()
            .RespondJson("""{"conversationId":"c1","title":null}""", System.Net.HttpStatusCode.Created)
            .RespondEvents(AgUi.RunStarted("c1"), AgUi.Text("one"))
            .RespondEvents(AgUi.Text("two"));
        var assistant = CreateAssistant(backend);
        var chat = new AssistantChat();

        // Act
        await RunAsync(assistant, chat, "first");
        await RunAsync(assistant, chat, "second");

        // Assert
        Assert.AreEqual("c1", chat.ConversationId);
        Assert.HasCount(3, backend.Calls, "one create, two chat calls");
        Assert.AreEqual(HttpMethod.Post, backend.Calls[0].Request.Method);
        StringAssert.EndsWith(backend.Calls[0].Request.RequestUri!.AbsolutePath, "/assistant/conversations");
        var second = Sent(backend, 2);
        Assert.AreEqual("c1", second.ConversationId);
        Assert.HasCount(1, second.Messages);
        Assert.AreEqual(new AssistantMessage(AssistantRoles.User, "second"), second.Messages[0]);
        Assert.HasCount(4, chat.Entries, "the tab still shows the whole chat");
    }

    [TestMethod]
    public async Task StoredChat_FixRequest_CarriesOnlyTheFix()
    {
        // Arrange
        var backend = new StubAssistantBackend().StoringChats()
            .RespondJson("""{"conversationId":"c1","title":null}""", System.Net.HttpStatusCode.Created)
            .RespondEvents(Proposal(InvalidXml))
            .RespondEvents(Proposal(ValidXml));
        var chat = new AssistantChat();

        // Act
        var proposal = await RunAsync(CreateAssistant(backend), chat);

        // Assert
        Assert.AreEqual(ValidXml, proposal);
        var fix = Sent(backend, 2);
        Assert.AreEqual("c1", fix.ConversationId);
        Assert.HasCount(1, fix.Messages, "the backend stored the rejected proposal itself");
        StringAssert.StartsWith(fix.Messages[0].Text, EditorAssistant.FixPrefix);
    }

    [TestMethod]
    public async Task StoredChat_CreateFails_EndsTheTurnWithTheError()
    {
        // Arrange
        var backend = new StubAssistantBackend().StoringChats().RespondStatus(System.Net.HttpStatusCode.ServiceUnavailable);
        var chat = new AssistantChat();

        // Act
        var proposal = await RunAsync(CreateAssistant(backend), chat);

        // Assert
        Assert.IsNull(proposal);
        Assert.AreEqual(AssistantErrorCodes.Unavailable, chat.LastError?.Code);
        Assert.IsNull(chat.ConversationId);
        Assert.HasCount(1, backend.Calls, "no chat call without a stored chat");
    }

    [TestMethod]
    public async Task StoredChat_ThatIsGone_IsForgotten_SoTheNextMessageStartsANewOne()
    {
        // Arrange
        var backend = new StubAssistantBackend().StoringChats()
            .RespondStatus(System.Net.HttpStatusCode.NotFound, """{"code":"conversation_not_found","message":"No such chat."}""")
            .RespondJson("""{"conversationId":"c2","title":null}""", System.Net.HttpStatusCode.Created)
            .RespondEvents(AgUi.Text("fresh"));
        var assistant = CreateAssistant(backend);
        var chat = new AssistantChat { ConversationId = "c1" };

        // Act
        await RunAsync(assistant, chat, "first");
        var afterGone = (chat.ConversationId, chat.LastError?.Code);
        await RunAsync(assistant, chat, "again");

        // Assert
        Assert.AreEqual((null, AssistantErrorCodes.ConversationNotFound), afterGone);
        Assert.AreEqual("c2", chat.ConversationId);
        Assert.AreEqual("c2", Sent(backend, 2).ConversationId);
    }

    [TestMethod]
    public async Task OpenChat_RestoresTheTurns_RecheckingEveryProposal()
    {
        // Arrange
        var history = new AssistantConversationHistory(
            new AssistantConversationInfo("c1", "Approval", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            [
                new(1, AssistantRoles.User, "approval process", null, DateTimeOffset.UnixEpoch),
                new(2, AssistantRoles.Assistant, "Draft.", InvalidXml, DateTimeOffset.UnixEpoch),
                new(3, AssistantRoles.User, EditorAssistant.FixPrefix + "broken", null, DateTimeOffset.UnixEpoch),
                new(4, AssistantRoles.Assistant, "Fixed.", ValidXml, DateTimeOffset.UnixEpoch),
            ]);
        var backend = new StubAssistantBackend().RespondJson(JsonSerializer.Serialize(history, AssistantProtocol.JsonOptions));
        var chat = new AssistantChat();
        chat.Add(new(AssistantRoles.User, "another chat's turn"));

        // Act
        await CreateAssistant(backend).OpenChatAsync(chat, "c1", CancellationToken.None);

        // Assert
        Assert.AreEqual("c1", chat.ConversationId);
        Assert.HasCount(4, chat.Entries, "the other chat's turns are gone");
        Assert.IsFalse(chat.Entries[1].ProposalValid);
        Assert.IsTrue(chat.Entries[2].Automatic);
        Assert.IsTrue(chat.Entries[3].ProposalValid);
        Assert.AreEqual(ValidXml, chat.Entries[3].ProposalXml);
    }

    [TestMethod]
    public async Task OpenChat_ThatIsGone_LeavesANewChatWithTheError()
    {
        // Arrange
        var backend = new StubAssistantBackend()
            .RespondStatus(System.Net.HttpStatusCode.NotFound, """{"code":"conversation_not_found","message":"No such chat."}""");
        var chat = new AssistantChat { ConversationId = "c1" };

        // Act
        await CreateAssistant(backend).OpenChatAsync(chat, "c1", CancellationToken.None);

        // Assert
        Assert.IsNull(chat.ConversationId);
        Assert.AreEqual(AssistantErrorCodes.ConversationNotFound, chat.LastError?.Code);
    }

    [TestMethod]
    public async Task DeleteChat_RemovesItAndEmptiesTheTab()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondStatus(System.Net.HttpStatusCode.NoContent);
        var chat = new AssistantChat { ConversationId = "c1" };
        chat.Add(new(AssistantRoles.User, "hi"));

        // Act
        var error = await CreateAssistant(backend).DeleteChatAsync(chat, CancellationToken.None);

        // Assert
        Assert.IsNull(error);
        Assert.IsNull(chat.ConversationId);
        Assert.IsEmpty(chat.Entries);
        Assert.AreEqual(HttpMethod.Delete, backend.Calls.Single().Request.Method);
    }

    [TestMethod]
    public async Task DeleteChat_Failing_KeepsTheChat()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondStatus(System.Net.HttpStatusCode.ServiceUnavailable);
        var chat = new AssistantChat { ConversationId = "c1" };
        chat.Add(new(AssistantRoles.User, "hi"));

        // Act
        var error = await CreateAssistant(backend).DeleteChatAsync(chat, CancellationToken.None);

        // Assert
        Assert.AreEqual(AssistantErrorCodes.Unavailable, error?.Code);
        Assert.AreEqual("c1", chat.ConversationId);
        Assert.HasCount(1, chat.Entries);
    }

    [TestMethod]
    public async Task UnreachableCapabilities_AreAskedAgain()
    {
        // Arrange
        var backend = new StubAssistantBackend { Capabilities = () => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway) };
        var assistant = CreateAssistant(backend);

        // Act
        var first = await assistant.StoresChatsAsync(CancellationToken.None);
        backend.StoringChats();
        var second = await assistant.StoresChatsAsync(CancellationToken.None);

        // Assert
        Assert.IsFalse(first);
        Assert.IsTrue(second);
        Assert.AreEqual(2, backend.CapabilitiesCalls);
    }

    [TestMethod]
    public void Clone_ContinuesTheSameStoredChat()
    {
        // Arrange
        var chat = new AssistantChat { ConversationId = "c1" };
        chat.Add(new(AssistantRoles.User, "hi"));

        // Act
        var copy = chat.Clone();

        // Assert
        Assert.AreEqual("c1", copy.ConversationId);
        Assert.HasCount(1, copy.Entries);
    }
}
