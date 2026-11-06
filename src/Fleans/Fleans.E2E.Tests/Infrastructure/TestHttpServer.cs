using System.Collections.Concurrent;
using System.Net;

namespace Fleans.E2E.Tests.Infrastructure;

/// <summary>
/// Minimal HttpListener-based server that specs can target from inside workflow custom-task
/// plugins. Binds to a random localhost port; serves GET /probe?key=&amp;event=&amp;silo=
/// which records lifecycle reports from the split-roles leg's <c>e2e-probe</c> plugin
/// (see <see cref="ProbeEvents"/>), and POST /assistant/chat — a canned assistant backend for the
/// editor's AI panel (<see cref="AssistantRequests"/>, #1015). With <see cref="AssistantStoresChats"/> on,
/// it also keeps chats in memory (#1017: /assistant/capabilities and /assistant/conversations). Uses HttpListener (built into
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

    /// <summary>
    /// Off (the default): the stub is a backend without stored chats — 404 on <c>/assistant/capabilities</c>,
    /// so the editor sends the whole conversation. On: it reports <c>persistence</c> and keeps chats in memory.
    /// The editor reads capabilities once per circuit, so a spec turns this on before opening the editor.
    /// </summary>
    public static volatile bool AssistantStoresChats;

    /// <summary>How long the stub waits before answering a chat call, so a spec can act mid-answer. Ticks, for volatile.</summary>
    public static long AssistantReplyDelayTicks;

    private sealed record StoredChat(string Id, string Title, List<object> Messages);

    private static readonly ConcurrentDictionary<string, StoredChat> _chats = new();

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
                string json;
                using (var reader = new StreamReader(context.Request.InputStream))
                    json = await reader.ReadToEndAsync();
                _assistantRequests.Enqueue(new AssistantCall(json, context.Request.Headers["Authorization"]));

                var events = "";
                using (var request = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = request.RootElement;
                    if (root.TryGetProperty("conversationId", out var id) && id.GetString() is { } chatId)
                    {
                        if (!_chats.TryGetValue(chatId, out var chat))
                        {
                            await WriteJsonAsync(context, 404, new { code = "conversation_not_found", message = "No such chat." });
                            return;
                        }
                        var text = root.GetProperty("messages")[0].GetProperty("text").GetString() ?? "";
                        lock (chat.Messages)
                        {
                            chat.Messages.Add(Stored(chat.Messages.Count + 1, "user", text, null));
                            chat.Messages.Add(Stored(chat.Messages.Count + 1, "assistant", AssistantReplyText, AssistantProposalXml));
                        }
                        events = SseEvent("conversation", new { conversationId = chat.Id, title = chat.Title });
                    }
                }
                await Task.Delay(TimeSpan.FromTicks(Interlocked.Read(ref AssistantReplyDelayTicks)));
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                var body = System.Text.Encoding.UTF8.GetBytes(events +
                    SseEvent("text", new { text = "Here is " }) +
                    SseEvent("text", new { text = "an approval process." }) +
                    SseEvent("proposal", new { bpmnXml = AssistantProposalXml }));
                await context.Response.OutputStream.WriteAsync(body);
            }
            else if (path.Equals("/assistant/capabilities", StringComparison.OrdinalIgnoreCase))
            {
                if (AssistantStoresChats)
                    await WriteJsonAsync(context, 200, new { contractVersion = 2, features = new[] { "bpmn", "persistence" } });
                else
                    context.Response.StatusCode = 404;
            }
            else if (path.StartsWith("/assistant/conversations", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConversationsAsync(context, path["/assistant/conversations".Length..].Trim('/'));
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

    // A tiny in-memory stand-in for a backend's stored chats; every chat is visible to every caller.
    private static async Task HandleConversationsAsync(HttpListenerContext context, string id)
    {
        var method = context.Request.HttpMethod;
        if (id.Length == 0 && method == "POST")
        {
            var chat = new StoredChat(Guid.NewGuid().ToString(), "E2E chat " + (_chats.Count + 1), new List<object>());
            _chats[chat.Id] = chat;
            await WriteJsonAsync(context, 201, new { conversationId = chat.Id, title = (string?)null });
        }
        else if (id.Length == 0 && method == "GET")
        {
            var items = _chats.Values.Select(c => new
            {
                conversationId = c.Id, title = c.Title, createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow,
            }).ToList();
            await WriteJsonAsync(context, 200, new { items, nextCursor = (string?)null });
        }
        else if (!_chats.TryGetValue(id, out var chat))
        {
            await WriteJsonAsync(context, 404, new { code = "conversation_not_found", message = "No such chat." });
        }
        else if (method == "GET")
        {
            List<object> messages;
            lock (chat.Messages)
                messages = chat.Messages.ToList();
            await WriteJsonAsync(context, 200, new
            {
                conversation = new { conversationId = chat.Id, title = chat.Title, createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow },
                messages,
            });
        }
        else if (method == "DELETE")
        {
            _chats.TryRemove(id, out _);
            context.Response.StatusCode = 204;
        }
        else
        {
            context.Response.StatusCode = 405;
        }
    }

    private static object Stored(int seq, string role, string text, string? bpmnXml) =>
        new { seq, role, text, bpmnXml, createdAt = DateTimeOffset.UtcNow };

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(body)));
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
