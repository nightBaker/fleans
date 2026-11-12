using System.Net;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Fleans.Assistant.Contracts;
using Fleans.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleans.Web.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> standing in for an assistant backend: answers each call with
/// the next queued response and records what the client sent. <c>GET /assistant/capabilities</c> is
/// answered apart, from <see cref="Capabilities"/> (404, a backend without stored chats, by default),
/// and is counted in <see cref="CapabilitiesCalls"/> rather than <see cref="Calls"/>.
/// </summary>
internal sealed class StubAssistantBackend : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

    /// <summary>The capabilities answer; <c>null</c> → 404.</summary>
    public Func<HttpResponseMessage>? Capabilities { get; set; }

    public int CapabilitiesCalls { get; private set; }

    /// <summary>A backend that stores chats (<c>persistence</c> in its capabilities).</summary>
    public StubAssistantBackend StoringChats()
    {
        Capabilities = () => Json(HttpStatusCode.OK, """{"contractVersion":2,"features":["bpmn","persistence"]}""");
        return this;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Queues a <c>200 application/json</c> answer.</summary>
    public StubAssistantBackend RespondJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond(() => Json(status, json));

    public StubAssistantBackend Respond(Func<HttpResponseMessage> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    /// <summary>
    /// Queues a <c>200 text/event-stream</c> AG-UI run answer: each item is an AG-UI <see cref="BaseEvent"/>, a sequence
    /// of them (e.g. <see cref="AgUi.Proposal"/>) or a raw <c>data:</c> string (for unreadable payloads).
    /// </summary>
    public StubAssistantBackend RespondEvents(params object[] items)
    {
        var body = new StringBuilder();
        foreach (var data in Flatten(items))
            body.Append("data: ").Append(data).Append("\n\n");
        return Respond(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream"),
        });
    }

    /// <summary>Queues a <c>200 text/event-stream</c> of <c>(id, event)</c>, as <c>/events</c> answers; the event is as in <see cref="RespondEvents"/>.</summary>
    public StubAssistantBackend RespondFeed(params (string Id, object Event)[] events)
    {
        var body = new StringBuilder();
        foreach (var (id, evt) in events)
            foreach (var data in Flatten([evt]))
                body.Append("id: ").Append(id).Append('\n').Append("data: ").Append(data).Append("\n\n");
        return Respond(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream"),
        });
    }

    private static IEnumerable<string> Flatten(IEnumerable<object> items)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case string raw:
                    yield return raw;
                    break;
                case BaseEvent evt:
                    yield return AgUi.Serialize(evt);
                    break;
                case IEnumerable<BaseEvent> many:
                    foreach (var evt in many)
                        yield return AgUi.Serialize(evt);
                    break;
                default:
                    throw new ArgumentException($"Not an AG-UI event: {item}");
            }
        }
    }

    /// <summary>The editor's request inside the run posted by call <paramref name="call"/>.</summary>
    public AssistantRequest SentRequest(int call = 0) =>
        AssistantAgUi.FromRunAgentInput(SentRun(call));

    /// <summary>The AG-UI run posted by call <paramref name="call"/>.</summary>
    public RunAgentInput SentRun(int call = 0) =>
        JsonSerializer.Deserialize(Calls[call].Body, AGUIJsonSerializerContext.Default.RunAgentInput)!;

    public StubAssistantBackend RespondStatus(HttpStatusCode status, string? jsonBody = null) =>
        Respond(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody ?? "", Encoding.UTF8, "application/json"),
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/assistant/capabilities", StringComparison.Ordinal))
        {
            CapabilitiesCalls++;
            return Capabilities?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        if (_responses.Count == 0)
            throw new InvalidOperationException("No response queued for this call.");
        return _responses.Dequeue()();
    }

    public HttpAssistantClient CreateClient(TimeSpan? timeout = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpAssistantClient.HttpClientName).Returns(_ => new HttpClient(this, disposeHandler: false));
        var options = Options.Create(new AssistantOptions
        {
            Endpoint = "http://assistant.test/base",
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        });
        return new HttpAssistantClient(factory, options, NullLogger<HttpAssistantClient>.Instance);
    }
}
