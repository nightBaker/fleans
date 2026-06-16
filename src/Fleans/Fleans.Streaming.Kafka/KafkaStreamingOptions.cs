using Confluent.Kafka;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Kafka security protocol, mirroring Confluent.Kafka's enum but kept Fleans-owned
/// so Confluent types stay out of the public API surface.
/// </summary>
public enum KafkaSecurityProtocol
{
    Plaintext,
    Ssl,
    SaslPlaintext,
    SaslSsl,
}

/// <summary>
/// SASL mechanism, mirroring Confluent.Kafka's enum.
/// </summary>
public enum KafkaSaslMechanism
{
    Plain,
    ScramSha256,
    ScramSha512,
    OAuthBearer,
}

/// <summary>
/// Kafka streaming options.
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

    public bool EnableDeadLetterQueue { get; set; } = false;

    public int MaxConsumerRetries { get; set; } = 3;

    public string DeadLetterTopicSuffix { get; set; } = "-dlq";

    /// <summary>
    /// Kafka security protocol. Defaults to <see cref="KafkaSecurityProtocol.Plaintext"/>
    /// for backward compatibility with existing plaintext deployments.
    /// </summary>
    public KafkaSecurityProtocol SecurityProtocol { get; set; } = KafkaSecurityProtocol.Plaintext;

    /// <summary>
    /// SASL mechanism. Required when <see cref="SecurityProtocol"/> is
    /// <see cref="KafkaSecurityProtocol.SaslPlaintext"/> or <see cref="KafkaSecurityProtocol.SaslSsl"/>.
    /// </summary>
    public KafkaSaslMechanism? SaslMechanism { get; set; }

    /// <summary>
    /// SASL username. Required for PLAIN, SCRAM-SHA-256, and SCRAM-SHA-512 mechanisms.
    /// </summary>
    public string? SaslUsername { get; set; }

    /// <summary>
    /// SASL password. Required for PLAIN, SCRAM-SHA-256, and SCRAM-SHA-512 mechanisms.
    /// </summary>
    public string? SaslPassword { get; set; }

    /// <summary>
    /// OAuthBearer token provider callback. Required when <see cref="SaslMechanism"/> is
    /// <see cref="KafkaSaslMechanism.OAuthBearer"/>. The callback is registered via
    /// <c>SetOAuthBearerTokenRefreshHandler</c> on each client builder.
    /// </summary>
    public Action<IClient, string>? OAuthBearerTokenProvider { get; set; }
}
