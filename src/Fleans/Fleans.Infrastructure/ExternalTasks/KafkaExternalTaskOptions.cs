namespace Fleans.Infrastructure.ExternalTasks;

/// <summary>
/// Kafka queue for external tasks (<c>Fleans:ExternalTasks:Kafka</c>, #979): one topic per task type,
/// <c>{TopicPrefix}{taskType}</c>, keyed by workflow instance id.
/// </summary>
public sealed class KafkaExternalTaskOptions
{
    public const string SectionName = "Fleans:ExternalTasks:Kafka";

    /// <summary>Bootstrap servers, e.g. <c>kafka:9092</c>. Required.</summary>
    public string Brokers { get; set; } = string.Empty;

    /// <summary>Topic name prefix; the task type is appended. Default <c>fleans.tasks.</c>.</summary>
    public string TopicPrefix { get; set; } = "fleans.tasks.";

    /// <summary>
    /// Create the topic of every configured task type at startup when it is missing. Default <c>true</c>;
    /// turn it off where topics are provisioned out of band.
    /// </summary>
    public bool CreateTopics { get; set; } = true;

    /// <summary>Partitions of a created topic — the most workers of one consumer group that share it. Default 8.</summary>
    public int NumPartitions { get; set; } = 8;

    /// <summary>Replication factor of a created topic. Default 3; a single-broker cluster needs 1.</summary>
    public short ReplicationFactor { get; set; } = 3;

    /// <summary>
    /// Extra librdkafka settings for the producer and the admin client, by their librdkafka names
    /// (<c>security.protocol</c>, <c>sasl.mechanism</c>, <c>ssl.ca.location</c>, …).
    /// </summary>
    public Dictionary<string, string> Config { get; set; } = new();
}
