using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Single source of truth for queue ↔ topic name mapping. Topics are named
/// <c>{TopicPrefix.TrimEnd('-')}-{queueIndex}</c> for <c>queueIndex</c> in
/// <c>[0, QueueCount)</c>. The mapper's <see cref="QueueId"/>.ToString() is
/// expected to match.
/// </summary>
internal static class KafkaTopicNaming
{
    public static string MapperPrefix(KafkaStreamingOptions options) => options.TopicPrefix.TrimEnd('-');

    public static string TopicForQueue(KafkaStreamingOptions options, QueueId queueId)
    {
        // QueueId.ToString() format from HashRingBasedStreamQueueMapper is "{prefix}-{index}".
        // Use it verbatim so producer and consumer agree without re-deriving the index.
        return queueId.ToString();
    }

    public static IEnumerable<string> AllExpectedTopics(KafkaStreamingOptions options)
    {
        var prefix = MapperPrefix(options);
        for (var i = 0; i < options.QueueCount; i++)
        {
            yield return $"{prefix}-{i}";
        }
    }
}
