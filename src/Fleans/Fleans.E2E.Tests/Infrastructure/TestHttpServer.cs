using System.Net;
using System.Text;
using System.Text.Json;

namespace Fleans.E2E.Tests.Infrastructure;

/// <summary>
/// Minimal HttpListener-based echo server that specs can target from inside workflow
/// custom-task plugins (RestCaller). Binds to a random localhost port; serves GET /echo
/// and GET /status/{code} for happy-path / non-2xx tests. Uses HttpListener (built into
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
            if (path.Equals("/echo", StringComparison.OrdinalIgnoreCase))
            {
                var requestId = context.Request.Headers["X-Request-Id"] ?? "";
                var body = JsonSerializer.Serialize(new
                {
                    method = context.Request.HttpMethod,
                    path,
                    requestId,
                    ok = true,
                });
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            else if (path.StartsWith("/status/", StringComparison.OrdinalIgnoreCase))
            {
                var code = int.TryParse(path["/status/".Length..], out var c) ? c : 500;
                context.Response.StatusCode = code;
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

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _listener.Close();
        _cts.Dispose();
    }
}
