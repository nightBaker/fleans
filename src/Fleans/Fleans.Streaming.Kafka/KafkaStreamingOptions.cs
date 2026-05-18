namespace Fleans.Streaming.Kafka;

/// <summary>
/// v1 Kafka streaming options. Plaintext brokers only — production SASL/TLS lands in a follow-up.
/// </summary>
public class KafkaStreamingOptions
{
    public string Brokers { get; set; } = string.Empty;

    public string ConsumerGroup { get; set; } = "fleans";

    public string TopicPrefix { get; set; } = "fleans-";

    /// <summary>
    /// Orleans pulling-agent count for the Kafka stream provider — one consumer-group
    /// consumer activates per queue, each subscribed to one topic. Matches the baseline
    /// of peer providers (Redis <c>TotalQueueCount</c>, AzureQueue <c>QueueNames</c> length).
    /// This is the Orleans-side parallelism knob; see reference/streaming.md "Tuning throughput".
    /// </summary>
    public int QueueCount { get; set; } = 8;

    /// <summary>
    /// Kafka-side per-topic partition count at topic creation. Independent of
    /// <see cref="QueueCount"/>: more partitions adds broker-side write parallelism and
    /// partition-level ordering, but does NOT multiply Orleans consumer parallelism
    /// (one <c>Subscribe</c>-mode consumer per topic regardless of partition count).
    /// Forward-only: <c>kafka-topics --alter --partitions N</c> can grow but not shrink.
    /// </summary>
    public int NumPartitions { get; set; } = 1;

    public short ReplicationFactor { get; set; } = 1;

    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan AdminTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
