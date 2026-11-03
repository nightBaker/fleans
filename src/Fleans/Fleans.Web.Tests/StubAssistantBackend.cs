using System.Net;
using System.Text;
using Fleans.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleans.Web.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> standing in for an assistant backend: answers each call with
/// the next queued response and records what the client sent.
/// </summary>
internal sealed class StubAssistantBackend : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

    public StubAssistantBackend Respond(Func<HttpResponseMessage> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    /// <summary>Queues a <c>200 text/event-stream</c> made of <c>(event, data)</c> pairs.</summary>
    public StubAssistantBackend RespondEvents(params (string Event, string Data)[] events)
    {
        var body = new StringBuilder();
        foreach (var (name, data) in events)
            body.Append("event: ").Append(name).Append('\n').Append("data: ").Append(data).Append("\n\n");
        return Respond(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream"),
        });
    }

    public StubAssistantBackend RespondStatus(HttpStatusCode status, string? jsonBody = null) =>
        Respond(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody ?? "", Encoding.UTF8, "application/json"),
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
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
