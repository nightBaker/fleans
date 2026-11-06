using Fleans.Application.CustomTasks;
using Fleans.Application.ExternalTasks;
using Fleans.Application.ExternalTasks.Grpc;
using Fleans.ExternalTasks.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fleans.Application.Tests.ExternalTasks;

/// <summary>
/// Real Fleans.ExternalTasks.Sdk workers for a test cluster: the worker gRPC service runs on an
/// in-process TestServer that calls the cluster through its client, as Fleans.Api does, and each
/// worker host talks to it through the TestServer's handler.
/// </summary>
internal sealed class GrpcExternalTaskWorkers(IClusterClient client, TimeSpan heartbeatInterval, TimeSpan executionTimeout)
    : IAsyncDisposable
{
    private readonly List<IHost> _workers = [];
    private WebApplication? _engine;

    public async Task<IHost> StartAsync(string workerName, Action<IServiceCollection> handlers, bool grpcWeb = false)
    {
        var handler = await GetEngineHandlerAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddFleansExternalTaskWorker(o =>
        {
            o.EngineUrl = new Uri("http://engine");
            o.WorkerName = workerName;
            o.UseGrpcWeb = grpcWeb;
            o.HttpHandler = handler;
            o.ReconnectDelay = TimeSpan.FromMilliseconds(200);
        });
        handlers(builder.Services);
        var host = builder.Build();
        await host.StartAsync();
        _workers.Add(host);
        return host;
    }

    /// <summary>Polls until the catalog lists exactly <paramref name="workers"/> for <paramref name="taskType"/>.</summary>
    public async Task WaitForCatalogWorkers(string taskType, params string[] workers)
    {
        var catalog = client.GetGrain<ICustomTaskCatalogGrain>(0);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var listed = (await catalog.Get(taskType))?.SiloNames.Order().ToList() ?? [];
            if (listed.SequenceEqual(workers.Order()))
                return;
            Assert.IsTrue(DateTime.UtcNow < deadline,
                $"Catalog lists [{string.Join(", ", listed)}] for '{taskType}', expected [{string.Join(", ", workers)}].");
            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers)
        {
            await worker.StopAsync();
            worker.Dispose();
        }
        if (_engine is not null)
            await _engine.DisposeAsync();
    }

    public async Task<HttpMessageHandler> GetEngineHandlerAsync()
    {
        if (_engine is null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddGrpc();
            builder.Services.AddSingleton<IGrainFactory>(client);
            builder.Services.AddSingleton<ExternalTaskWorkerConnections>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.Configure<ExternalTaskOptions>(o =>
            {
                o.HeartbeatInterval = heartbeatInterval;
                o.WorkerTimeout = heartbeatInterval * 3;
                o.ExecutionTimeout = executionTimeout;
            });
            _engine = builder.Build();
            _engine.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
            _engine.MapGrpcService<ExternalTaskWorkerGrpcService>();
            await _engine.StartAsync();
        }
        return _engine.GetTestServer().CreateHandler();
    }
}
