using System.Net;
using System.Text.Json;
using Fleans.Assistant.Contracts;

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
            ("text", """{"text":"Here is "}"""),
            ("text", """{"text":"a draft."}"""),
            ("proposal", """{"bpmnXml":"<definitions/>"}"""));
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
    public async Task PostsTheCamelCaseRequestToTheChatPath()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(("text", """{"text":"ok"}"""));
        var client = backend.CreateClient();

        // Act
        await client.SendAsync(Request(), _ => Task.CompletedTask, CancellationToken.None);

        // Assert
        var (request, body) = backend.Calls.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("http://assistant.test/base/assistant/chat", request.RequestUri!.ToString());
        Assert.IsTrue(request.Headers.Accept.Any(a => a.MediaType == "text/event-stream"));
        using var json = JsonDocument.Parse(body);
        Assert.AreEqual("1.2.3", json.RootElement.GetProperty("engineVersion").GetString());
        Assert.AreEqual("user", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.AreEqual("rest-call", json.RootElement.GetProperty("plugins")[0].GetProperty("taskType").GetString());
        Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("currentXml").ValueKind);
    }

    [TestMethod]
    public async Task ErrorEvent_EndsTheReply_KeepingTheTextSoFar()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondEvents(
            ("text", """{"text":"Working"}"""),
            ("error", """{"code":"model_overloaded","message":"Try later"}"""),
            ("proposal", """{"bpmnXml":"<never/>"}"""));
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
            ("thinking", """{"anything":1}"""),
            ("proposal", """{"bpmnXml":"<first/>"}"""),
            ("proposal", """{"bpmnXml":"<second/>"}"""))
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
        var client = new StubAssistantBackend().RespondEvents(("text", "not json")).CreateClient();

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
