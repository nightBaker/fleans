using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleans.Plugins.RestCaller.Tests;

/// <summary>
/// Hermetic Kestrel mock server. Each test gets a fresh instance bound to a random
/// ephemeral port; the test passes the resolved <see cref="BaseUrl"/> into its inputs.
/// </summary>
internal sealed class MockServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string BaseUrl { get; }

    public List<RecordedRequest> Requests { get; } = new();

    public Func<HttpContext, Task>? Handler { get; set; }

    private MockServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<MockServer> StartAsync(Func<HttpContext, Task>? handler = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(opts =>
        {
            opts.Listen(System.Net.IPAddress.Loopback, 0);
        });

        var app = builder.Build();

        MockServer? self = null;
        app.MapFallback(async ctx =>
        {
            var rec = await RecordedRequest.CaptureAsync(ctx);
            self!.Requests.Add(rec);
            if (self.Handler is { } h)
                await h(ctx);
            else
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        var server = new MockServer(app, url) { Handler = handler };
        self = server;
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed record RecordedRequest(
    string Method,
    string Path,
    Dictionary<string, string> Headers,
    string Body)
{
    public static async Task<RecordedRequest> CaptureAsync(HttpContext ctx)
    {
        var headers = ctx.Request.Headers
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value!), StringComparer.OrdinalIgnoreCase);
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;
        return new RecordedRequest(ctx.Request.Method, ctx.Request.Path.Value ?? "/", headers, body);
    }
}
