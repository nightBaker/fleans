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

    private static (string Event, string Data) Proposal(string xml) =>
        ("proposal", JsonSerializer.Serialize(new AssistantProposal(xml), AssistantProtocol.JsonOptions));

    private static Task<string?> RunAsync(EditorAssistant assistant, AssistantConversation conversation, string text = "approval process") =>
        assistant.RunTurnAsync(conversation, text, currentXml: null, _ => Task.CompletedTask, () => Task.CompletedTask, CancellationToken.None);

    [TestMethod]
    public async Task ValidProposal_IsReturnedAndOffered()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(("text", """{"text":"Done."}"""), Proposal(ValidXml));
        var conversation = new AssistantConversation();

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
            .RespondEvents(("text", """{"text":"Fixed."}"""), Proposal(ValidXml));
        var conversation = new AssistantConversation();

        // Act
        var proposal = await RunAsync(CreateAssistant(backend), conversation);

        // Assert
        Assert.AreEqual(ValidXml, proposal);
        Assert.AreEqual(2, backend.Calls.Count);
        var fixRequest = JsonSerializer.Deserialize<AssistantRequest>(backend.Calls[1].Body, AssistantProtocol.JsonOptions)!;
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
        var conversation = new AssistantConversation();

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
        var conversation = new AssistantConversation();

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
        var backend = new StubAssistantBackend().RespondEvents(("error", """{"code":"policy","message":"Not allowed"}"""));
        var conversation = new AssistantConversation();

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
            .RespondEvents(("text", """{"text":"Back."}"""));
        var assistant = CreateAssistant(backend);
        var conversation = new AssistantConversation();
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
        var backend = new StubAssistantBackend().RespondEvents(("text", """{"text":"ok"}"""));
        var assistant = CreateAssistant(backend, [
            new CustomTaskCatalogEntry("rest-call", "REST call", schema, ["silo-1"]),
            new CustomTaskCatalogEntry("bare", null, null, ["silo-1"])]);

        // Act
        await RunAsync(assistant, new AssistantConversation());

        // Assert
        var sent = JsonSerializer.Deserialize<AssistantRequest>(backend.Calls[0].Body, AssistantProtocol.JsonOptions)!;
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
        var conversation = new AssistantConversation();
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
}
