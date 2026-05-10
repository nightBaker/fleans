namespace Fleans.Streaming.AzureQueue;

/// <summary>
/// Configuration options for the Azure Queue Storage stream provider.
/// Bound from configuration section <c>Fleans:Streaming:AzureQueue</c>.
/// </summary>
public class AzureQueueStreamingOptions
{
    /// <summary>Full Azure Storage connection string. Use for local dev with Azurite.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Azure Storage account name. When set, Managed Identity (<see cref="Azure.Identity.DefaultAzureCredential"/>) is used instead of a connection string.</summary>
    public string? AccountName { get; set; }

    /// <summary>
    /// Names of the Azure queues that back the Orleans stream provider.
    /// Defaults to 8 queues (<c>fleans-stream-0</c> … <c>fleans-stream-7</c>).
    /// Orleans distributes stream work across these queues in parallel.
    /// </summary>
    public IList<string> QueueNames { get; set; } =
        Enumerable.Range(0, 8).Select(i => $"fleans-stream-{i}").ToList();

    /// <summary>Receiver lease timeout. Null uses the Azure SDK default (30 s).</summary>
    public TimeSpan? MessageVisibilityTimeout { get; set; }

    /// <summary>Number of messages pulled per polling cycle. Defaults to 32.</summary>
    public int PullingAgentBatchSize { get; set; } = 32;
}
