namespace Fleans.Streaming.Kafka;

/// <summary>
/// v1 Kafka streaming options. Plaintext brokers only — production SASL/TLS lands in a follow-up.
/// </summary>
public class KafkaStreamingOptions
{
    public string Brokers { get; set; } = string.Empty;

    public string ConsumerGroup { get; set; } = "fleans";

    public string TopicPrefix { get; set; } = "fleans-";

    public int QueueCount { get; set; } = 1;

    public int NumPartitions { get; set; } = 1;

    public short ReplicationFactor { get; set; } = 1;

    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan AdminTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
