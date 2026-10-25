using System.Diagnostics.Metrics;
using Confluent.Kafka;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// <c>fleans.kafka.consumer.lag</c>: per topic partition, how many records sit past the last one
/// this silo's receiver read. Reported in the "Fleans" meter — the name OpenTelemetry is wired to
/// (<c>FleansDiagnostics.MeterName</c>; this package does not reference Fleans.Application.Abstractions,
/// and meters are matched by name).
/// </summary>
/// <remarks>
/// Cheap by construction: the high watermark comes from <see cref="IConsumer{TKey,TValue}.GetWatermarkOffsets"/>,
/// the client's cached value from its last fetch — no broker round trip. Sampled once per poll that
/// returned records, so the value is the backlog seen at the last read; an idle partition keeps its
/// last value (normally 0).
/// </remarks>
internal static class KafkaConsumerLag
{
    private static readonly Meter Meter = new("Fleans", "1.0.0");

    private static readonly Gauge<long> Lag = Meter.CreateGauge<long>(
        "fleans.kafka.consumer.lag",
        unit: "{records}",
        description: "Kafka records past the last one the stream receiver read, per topic partition (from the cached high watermark).");

    /// <summary>
    /// Records left after <paramref name="lastReadOffset"/>: <c>high − (lastReadOffset + 1)</c>, never negative;
    /// <c>null</c> while the client has no high watermark yet.
    /// </summary>
    internal static long? Compute(WatermarkOffsets watermarks, long lastReadOffset)
    {
        var high = watermarks.High.Value;
        if (high < 0)
            return null; // Offset.Unset: no fetch response has carried it yet
        return Math.Max(0, high - (lastReadOffset + 1));
    }

    internal static void Record(IConsumer<byte[], byte[]> consumer, TopicPartition partition, long lastReadOffset)
    {
        if (Compute(consumer.GetWatermarkOffsets(partition), lastReadOffset) is not { } lag)
            return;
        Lag.Record(lag,
            new KeyValuePair<string, object?>("topic", partition.Topic),
            new KeyValuePair<string, object?>("partition", partition.Partition.Value));
    }
}
