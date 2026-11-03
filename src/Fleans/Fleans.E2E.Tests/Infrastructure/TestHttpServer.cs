using System.Collections.Concurrent;
using System.Net;

namespace Fleans.E2E.Tests.Infrastructure;

/// <summary>
/// Minimal HttpListener-based server that specs can target from inside workflow custom-task
/// plugins. Binds to a random localhost port; serves GET /probe?key=&amp;event=&amp;silo=
/// which records lifecycle reports from the split-roles leg's <c>e2e-probe</c> plugin
/// (see <see cref="ProbeEvents"/>), and POST /assistant/chat — a canned assistant backend for the
/// editor's AI panel (<see cref="AssistantRequests"/>, #1015). Uses HttpListener (built into
/// System.Net) rather than Kestrel so the test project doesn't need
/// Microsoft.NET.Sdk.Web.
/// </summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    private TestHttpServer(HttpListener listener, CancellationTokenSource cts, Task loop, string baseUrl)
    {
        _listener = listener;
        _cts = cts;
        _loop = loop;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>A lifecycle report from the <c>e2e-probe</c> plugin (Fleans.E2E.PluginHost).</summary>
    public sealed record ProbeEvent(string Key, string Event, string Silo, DateTimeOffset ReceivedAt);

    private static readonly ConcurrentQueue<ProbeEvent> _probeEvents = new();

    /// <summary>Probe reports for <paramref name="key"/>, in arrival order.</summary>
    public static IReadOnlyList<ProbeEvent> ProbeEvents(string key) =>
        _probeEvents.Where(e => e.Key == key).ToList();

    /// <summary>The process the stub assistant proposes: start → user task → end, without diagram layout.</summary>
    public const string AssistantProcessId = "assistant_e2e_approval";

    public const string AssistantReplyText = "Here is an approval process.";

    /// <summary>One call of the editor to <c>/assistant/chat</c>: its raw JSON body and <c>Authorization</c> header.</summary>
    public sealed record AssistantCall(string Body, string? Authorization);

    private static readonly ConcurrentQueue<AssistantCall> _assistantRequests = new();

    /// <summary>The editor's calls to <c>/assistant/chat</c>, in arrival order.</summary>
    public static IReadOnlyList<AssistantCall> AssistantRequests => _assistantRequests.ToList();

    public static TestHttpServer Start()
    {
        // Find a free port by binding to :0 via TcpListener.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var cts = new CancellationTokenSource();
        var loop = Task.Run(() => RunAsync(listener, cts.Token));

        return new TestHttpServer(listener, cts, loop, prefix.TrimEnd('/'));
    }

    private static async Task RunAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => HandleAsync(context), ct);
        }
    }

    private static async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/probe", StringComparison.OrdinalIgnoreCase))
            {
                var q = context.Request.QueryString;
                _probeEvents.Enqueue(new ProbeEvent(
                    q["key"] ?? "", q["event"] ?? "", q["silo"] ?? "", DateTimeOffset.UtcNow));
                context.Response.StatusCode = 204;
            }
            else if (path.Equals("/assistant/chat", StringComparison.OrdinalIgnoreCase)
                     && context.Request.HttpMethod == "POST")
            {
                using (var reader = new StreamReader(context.Request.InputStream))
                {
                    _assistantRequests.Enqueue(new AssistantCall(
                        await reader.ReadToEndAsync(), context.Request.Headers["Authorization"]));
                }
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                var body = System.Text.Encoding.UTF8.GetBytes(
                    SseEvent("text", new { text = "Here is " }) +
                    SseEvent("text", new { text = "an approval process." }) +
                    SseEvent("proposal", new { bpmnXml = AssistantProposalXml }));
                await context.Response.OutputStream.WriteAsync(body);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
        catch
        {
            // Swallow handler errors — this is a test stub, not production.
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static string SseEvent(string name, object data) =>
        $"event: {name}\ndata: {System.Text.Json.JsonSerializer.Serialize(data)}\n\n";

    private const string AssistantProposalXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="assistant_defs" targetNamespace="http://bpmn.io/schema/bpmn">
          <process id="{AssistantProcessId}" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="Approve request" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _listener.Close();
        _cts.Dispose();
    }
}
