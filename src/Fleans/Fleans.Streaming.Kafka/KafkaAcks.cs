namespace Fleans.Streaming.Kafka;

/// <summary>
/// Producer acknowledgement mode. Mirrors <see cref="Confluent.Kafka.Acks"/> without exposing
/// the Confluent dependency at the options layer.
/// </summary>
public enum KafkaAcks
{
    /// <summary>Broker leader + all in-sync replicas must acknowledge. Required for idempotent producers.</summary>
    All,
    /// <summary>Only the partition leader acknowledges. Lower latency, reduced durability.</summary>
    Leader,
    /// <summary>No acknowledgement. Highest throughput, fire-and-forget.</summary>
    None,
}
