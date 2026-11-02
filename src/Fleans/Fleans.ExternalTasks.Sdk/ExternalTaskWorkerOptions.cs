namespace Fleans.ExternalTasks.Sdk;

/// <summary>How the worker reaches the engine, bound from <c>Fleans:ExternalTaskWorker</c>.</summary>
public sealed class ExternalTaskWorkerOptions
{
    public const string SectionName = "Fleans:ExternalTaskWorker";

    /// <summary>The Fleans API base URL, e.g. <c>http://fleans-core:8080</c>. Required.</summary>
    public Uri? EngineUrl { get; set; }

    /// <summary>
    /// The name the catalog lists this worker under. Unique per process; defaults to
    /// <c>{machine}-{process id}</c> (on Kubernetes the machine name is the pod name).
    /// </summary>
    public string WorkerName { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}".ToLowerInvariant();

    /// <summary>
    /// Speak gRPC-Web (HTTP/1.1) instead of gRPC (HTTP/2). Default <c>true</c>: it works against
    /// any Fleans API endpoint and through HTTP/1.1 proxies. Set <c>false</c> when the endpoint
    /// speaks HTTP/2 — TLS, or a Kestrel endpoint configured with <c>Protocols=Http2</c>.
    /// </summary>
    public bool UseGrpcWeb { get; set; } = true;

    /// <summary>
    /// Bearer token sent with every call, for an API with <c>Authentication:Authority</c> set.
    /// <see cref="AccessTokenProvider"/> wins when both are set.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>Supplies a (fresh) bearer token per call, e.g. from a client-credentials flow.</summary>
    public Func<CancellationToken, ValueTask<string?>>? AccessTokenProvider { get; set; }

    /// <summary>Tasks run at the same time; further assignments wait in this process. Default 32.</summary>
    public int MaxConcurrentTasks { get; set; } = 32;

    /// <summary>Pause before re-subscribing after the connection to the engine drops. Default 5 s.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The innermost HTTP handler of the gRPC channel. Leave <c>null</c> for a default
    /// <see cref="SocketsHttpHandler"/>; set it for tests (an ASP.NET Core TestServer's handler)
    /// or custom TLS / proxy settings.
    /// </summary>
    public HttpMessageHandler? HttpHandler { get; set; }
}
