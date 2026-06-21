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
    /// Default cap on the response body the REST-call plugin will buffer into
    /// workflow state. 8 MiB is large enough for typical REST envelopes while
    /// keeping a single misbehaving upstream from filling the silo heap with a
    /// multi-GB response (the BCL default is 2 GiB). Operators with legitimate
    /// large-payload use cases can replace the typed-client registration with
    /// their own <c>AddHttpClient&lt;RestCallerHandler&gt;</c> call before
    /// <see cref="AddRestCallerPlugin"/>.
    /// </summary>
    public const long DefaultMaxResponseContentBufferSize = 8L * 1024 * 1024;

    public static IServiceCollection AddRestCallerPlugin(this IServiceCollection services)
    {
        // Typed-client; HttpClient.Timeout = InfiniteTimeSpan because per-call timeout is
        // enforced by the handler via CancellationTokenSource.CancelAfter(timeoutSec).
        // MaxResponseContentBufferSize caps ReadAsStringAsync / ReadAsByteArrayAsync so
        // a hostile or compromised upstream cannot stream gigabytes into the silo heap.
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
