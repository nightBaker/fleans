using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using Orleans.Streaming.AzureStorage;

namespace Fleans.Streaming.AzureQueue;

public static class AzureQueueSiloBuilderExtensions
{
    /// <summary>
    /// Registers the Azure Queue Storage-backed Orleans stream provider under the given name.
    /// Reads <see cref="AzureQueueStreamingOptions"/> from the provided configuration section.
    /// Requires exactly one of <c>ConnectionString</c> (dev/Azurite) or <c>AccountName</c>
    /// (Managed Identity, production).
    /// </summary>
    public static ISiloBuilder AddAzureQueueStreaming(
        this ISiloBuilder builder,
        string name,
        IConfigurationSection config)
    {
        var opts = config.Get<AzureQueueStreamingOptions>() ?? new();

        if (string.IsNullOrEmpty(opts.ConnectionString) && string.IsNullOrEmpty(opts.AccountName))
            throw new InvalidOperationException(
                "Fleans:Streaming:AzureQueue requires either ConnectionString or AccountName. " +
                "Set ConnectionString for local dev (Azurite) or AccountName for Managed Identity.");

        return builder.AddAzureQueueStreams(name, ob => ob.Configure(queueOptions =>
        {
            queueOptions.QueueServiceClient = string.IsNullOrEmpty(opts.ConnectionString)
                ? new QueueServiceClient(
                    new Uri($"https://{opts.AccountName}.queue.core.windows.net"),
                    new DefaultAzureCredential())
                : new QueueServiceClient(opts.ConnectionString);

            queueOptions.QueueNames = opts.QueueNames.ToList();

            if (opts.MessageVisibilityTimeout.HasValue)
                queueOptions.MessageVisibilityTimeout = opts.MessageVisibilityTimeout.Value;
        }));
    }
}
