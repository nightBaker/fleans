using Orleans;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Orleans <see cref="StreamSequenceToken"/> backed by a Kafka offset.
/// EventIndex disambiguates multiple events inside the same batch (single Kafka message → many domain events).
/// Built only from Kafka coordinates (offset, partition, event index), so the same record yields an equal
/// token in every silo and after every restart — Orleans keeps tokens across queue rebalances.
/// Known limitation: offsets are ordered only within one partition, so with <c>NumPartitions &gt; 1</c>
/// <see cref="CompareTo"/> is a stable total order but not the consumption order across partitions.
/// </summary>
[GenerateSerializer]
public sealed class KafkaSequenceToken : StreamSequenceToken
{
    [Id(0)]
    public override long SequenceNumber { get; protected set; }

    [Id(1)]
    public override int EventIndex { get; protected set; }

    [Id(2)]
    public int Partition { get; private set; }

    public KafkaSequenceToken()
    {
    }

    public KafkaSequenceToken(long offset, int eventIndex = 0, int partition = 0)
    {
        SequenceNumber = offset;
        EventIndex = eventIndex;
        Partition = partition;
    }

    public long Offset => SequenceNumber;

    /// <summary>Token for event <paramref name="eventIndex"/> of the same record.</summary>
    public KafkaSequenceToken WithEventIndex(int eventIndex) =>
        new(SequenceNumber, eventIndex, Partition);

    public override int CompareTo(StreamSequenceToken? other)
    {
        if (other is not KafkaSequenceToken k) return 1;
        var c = SequenceNumber.CompareTo(k.SequenceNumber);
        if (c != 0) return c;
        c = Partition.CompareTo(k.Partition);
        return c != 0 ? c : EventIndex.CompareTo(k.EventIndex);
    }

    public override bool Equals(StreamSequenceToken? other) =>
        other is KafkaSequenceToken k
        && SequenceNumber == k.SequenceNumber
        && EventIndex == k.EventIndex
        && Partition == k.Partition;

    public override bool Equals(object? obj) => Equals(obj as StreamSequenceToken);

    public override int GetHashCode() => HashCode.Combine(SequenceNumber, EventIndex, Partition);
}
