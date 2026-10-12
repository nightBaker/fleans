using Orleans;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Orleans <see cref="StreamSequenceToken"/> backed by a Kafka offset.
/// EventIndex disambiguates multiple events inside the same batch (single Kafka message → many domain events).
/// Ordering uses <see cref="ReceiveOrder"/>, not the offset: offsets are only ordered within one
/// partition, so with <c>NumPartitions &gt; 1</c> they say nothing about consumption order.
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

    /// <summary>Monotonic arrival position assigned by the receiver; the primary sort key.</summary>
    [Id(3)]
    public long ReceiveOrder { get; private set; }

    public KafkaSequenceToken()
    {
    }

    public KafkaSequenceToken(long offset, int eventIndex = 0, int partition = 0, long receiveOrder = 0)
    {
        SequenceNumber = offset;
        EventIndex = eventIndex;
        Partition = partition;
        ReceiveOrder = receiveOrder;
    }

    public long Offset => SequenceNumber;

    /// <summary>Token for event <paramref name="eventIndex"/> of the same record.</summary>
    public KafkaSequenceToken WithEventIndex(int eventIndex) =>
        new(SequenceNumber, eventIndex, Partition, ReceiveOrder);

    public override int CompareTo(StreamSequenceToken? other)
    {
        if (other is not KafkaSequenceToken k) return 1;
        var c = ReceiveOrder.CompareTo(k.ReceiveOrder);
        if (c != 0) return c;
        c = EventIndex.CompareTo(k.EventIndex);
        if (c != 0) return c;
        // Tie-breakers keep CompareTo consistent with Equals for tokens built without a ReceiveOrder.
        c = Partition.CompareTo(k.Partition);
        return c != 0 ? c : SequenceNumber.CompareTo(k.SequenceNumber);
    }

    public override bool Equals(StreamSequenceToken? other) =>
        other is KafkaSequenceToken k
        && ReceiveOrder == k.ReceiveOrder
        && SequenceNumber == k.SequenceNumber
        && EventIndex == k.EventIndex
        && Partition == k.Partition;

    public override bool Equals(object? obj) => Equals(obj as StreamSequenceToken);

    public override int GetHashCode() => HashCode.Combine(ReceiveOrder, SequenceNumber, EventIndex, Partition);
}
