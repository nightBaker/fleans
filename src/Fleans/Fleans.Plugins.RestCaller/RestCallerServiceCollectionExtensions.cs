using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Plugins.RestCaller;

/// <summary>
/// Wires the REST caller plugin into a Worker silo's host.
/// Registers a typed <see cref="HttpClient"/> for <see cref="RestCallerHandler"/>
/// (per-call timeout enforced via CancellationToken so the client itself stays
/// per-message-handler-pool-managed) and announces the plugin to the catalog.
/// </summary>
public static class RestCallerServiceCollectionExtensions
{
    /// <summary>
    /// Default ceiling on the response body the REST-call plugin will buffer into
    /// workflow state; a task's <c>maxResponseBytes</c> input can only lower it. 8 MiB is large enough for typical REST envelopes while
    /// keeping a single misbehaving upstream from filling the silo heap with a
    /// multi-GB response (the BCL default is 2 GiB). Operators with legitimate
    /// large-payload use cases can raise it with their own
    /// <c>AddHttpClient&lt;RestCallerHandler&gt;(c =&gt; c.MaxResponseContentBufferSize = …)</c>
    /// call registered <b>after</b> <see cref="AddRestCallerPlugin"/> — HttpClient configure
    /// actions run in registration order, so one registered before is overwritten.
    /// </summary>
    public const long DefaultMaxResponseContentBufferSize = 8L * 1024 * 1024;

    public static IServiceCollection AddRestCallerPlugin(this IServiceCollection services)
    {
        // Typed-client; HttpClient.Timeout = InfiniteTimeSpan because per-call timeout is
        // enforced by the handler via CancellationTokenSource.CancelAfter(timeoutSec).
        // MaxResponseContentBufferSize is the silo-wide body ceiling. The handler reads it and
        // enforces min(ceiling, maxResponseBytes) itself with a bounded stream read (it sends with
        // ResponseHeadersRead, so HttpClient does not apply the cap on its own).
        services.AddHttpClient<RestCallerHandler>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = DefaultMaxResponseContentBufferSize;
        });

        services.AddCustomTaskPlugin<RestCallerHandler>(
            taskType: "rest-call",
            displayName: "REST Caller",
            parameterSchema: RestCallerSchema.Default);

        return services;
    }
}
