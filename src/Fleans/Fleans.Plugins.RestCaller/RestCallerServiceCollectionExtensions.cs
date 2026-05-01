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
    public static IServiceCollection AddRestCallerPlugin(this IServiceCollection services)
    {
        // Typed-client; HttpClient.Timeout = InfiniteTimeSpan because per-call timeout is
        // enforced by the handler via CancellationTokenSource.CancelAfter(timeoutSec).
        services.AddHttpClient<RestCallerHandler>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.AddCustomTaskPlugin<RestCallerHandler>(
            taskType: "rest-call",
            displayName: "REST Caller",
            parameterSchema: RestCallerSchema.Default);

        return services;
    }
}
