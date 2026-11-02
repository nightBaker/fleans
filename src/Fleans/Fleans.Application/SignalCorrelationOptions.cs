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
    /// How many instances the delivery grain of one bucket signals at a time (#976). A broadcast
    /// publishes every bucket's subscriptions to the stream and returns; the delivery grains then
    /// keep at most <c>BucketCount × DeliveryConcurrency</c> deliveries in flight, so the pace of a
    /// broadcast to many instances — and its load on the journal and the read-model projector —
    /// is bounded however many subscribers it has. One at a time delivered 20 000 subscribers fastest
    /// and gentlest on the load-test stack (tests/load signal-fanout). Only the silos read it. Default 1.
    /// </summary>
    public int DeliveryConcurrency { get; set; } = 1;

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
