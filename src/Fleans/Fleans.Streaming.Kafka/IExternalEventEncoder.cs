namespace Fleans.Streaming.Kafka;

// Non-generic by design: a generic IExternalEventEncoder<T> would require one DI
// registration per event type, forcing the adapter to enumerate all T-registrations.
// Instead, the single non-generic interface handles dispatch internally — implementors
// check "@event is TEventType evt" and return null for unrecognized types.
// #685B (Avro) and #685C (Protobuf) register one IExternalEventEncoder each.
public interface IExternalEventEncoder
{
    Task<byte[]?> EncodeAsync(object @event, CancellationToken ct = default);
}
