using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

[GenerateSerializer]
public sealed class KafkaBatchContainer : IBatchContainer
{
    [Id(0)]
    public StreamId StreamId { get; set; }

    [Id(1)]
    public List<object> Events { get; set; } = new();

    [Id(2)]
    public Dictionary<string, object>? RequestContext { get; set; }

    [Id(3)]
    public KafkaSequenceToken? Token { get; set; }

    public StreamSequenceToken SequenceToken => Token ?? new KafkaSequenceToken(0, 0);

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        var baseOffset = Token?.SequenceNumber ?? 0;
        return Events
            .Select((evt, idx) => (evt, idx))
            .Where(t => t.evt is T)
            .Select(t => Tuple.Create((T)t.evt!, (StreamSequenceToken)new KafkaSequenceToken(baseOffset, t.idx)));
    }

    public bool ImportRequestContext()
    {
        if (RequestContext is null || RequestContext.Count == 0)
        {
            return false;
        }
        RequestContextExtensions.Import(RequestContext);
        return true;
    }
}
