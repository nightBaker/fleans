namespace Fleans.Application;

/// <summary>
/// Spread of signal subscriptions over correlation grains, bound from <c>Fleans:Signals</c>.
/// </summary>
public sealed class SignalCorrelationOptions
{
    public const string SectionName = "Fleans:Signals";

    /// <summary>
    /// How many buckets (one <c>SignalCorrelationGrain</c> each) the subscriptions of one signal
    /// name are spread over, by a stable hash of the workflow instance id. A broadcast calls
    /// every bucket in parallel. Every process that subscribes or broadcasts — silos, API, Web,
    /// MCP — must use the same value, and it may change only on an empty cluster: a
    /// subscription stored under one count is not found by a broadcast under another. Default 64.
    /// </summary>
    public int BucketCount { get; set; } = 64;

    /// <summary>
    /// A bucket with more subscribers than this hands its deliveries to the
    /// <c>events.SignalDelivery</c> stream, one event per subscriber keyed by its instance id,
    /// instead of calling every instance from its own turn (#976). A broadcast then returns once
    /// the deliveries are published, and the stream retries a delivery that failed transiently.
    /// Smaller buckets call their instances directly. Only the silos read it. Default 200.
    /// </summary>
    public int StreamDeliveryThreshold { get; set; } = 200;

    /// <summary>The bucket holding <paramref name="workflowInstanceId"/>'s subscriptions.</summary>
    public int BucketFor(Guid workflowInstanceId) => BucketFor(workflowInstanceId, BucketCount);

    /// <summary>
    /// FNV-1a over the id's 16 bytes, modulo <paramref name="bucketCount"/>. Stable across
    /// processes, runtimes and restarts — unlike <see cref="Guid.GetHashCode"/>, which is not
    /// guaranteed to be — because the bucket is part of the stored row key.
    /// </summary>
    public static int BucketFor(Guid workflowInstanceId, int bucketCount)
    {
        Span<byte> bytes = stackalloc byte[16];
        workflowInstanceId.TryWriteBytes(bytes);

        var hash = 2166136261u;
        foreach (var b in bytes)
            hash = (hash ^ b) * 16777619u;

        return (int)(hash % (uint)bucketCount);
    }
}
