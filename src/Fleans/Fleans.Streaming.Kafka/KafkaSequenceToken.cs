using Orleans;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Orleans <see cref="StreamSequenceToken"/> backed by a Kafka offset.
/// EventIndex disambiguates multiple events inside the same batch (single Kafka message → many domain events).
/// </summary>
[GenerateSerializer]
public sealed class KafkaSequenceToken : StreamSequenceToken
{
    [Id(0)]
    public override long SequenceNumber { get; protected set; }

    [Id(1)]
    public override int EventIndex { get; protected set; }

    public KafkaSequenceToken()
    {
    }

    public KafkaSequenceToken(long offset, int eventIndex = 0)
    {
        SequenceNumber = offset;
        EventIndex = eventIndex;
    }

    public long Offset => SequenceNumber;

    public override int CompareTo(StreamSequenceToken? other)
    {
        if (other is not KafkaSequenceToken k) return 1;
        var c = SequenceNumber.CompareTo(k.SequenceNumber);
        return c != 0 ? c : EventIndex.CompareTo(k.EventIndex);
    }

    public override bool Equals(StreamSequenceToken? other) =>
        other is KafkaSequenceToken k && SequenceNumber == k.SequenceNumber && EventIndex == k.EventIndex;

    public override bool Equals(object? obj) => Equals(obj as StreamSequenceToken);

    public override int GetHashCode() => HashCode.Combine(SequenceNumber, EventIndex);
}
