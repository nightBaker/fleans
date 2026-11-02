namespace Fleans.Application.Grains;

/// <summary>
/// Delivers the signal pages one bucket handed to the stream, keyed like the bucket's stream
/// (<c>{bucket}:{signalName}</c>). It is driven only by its
/// <see cref="Fleans.Domain.Events.SignalDeliveryPage"/> stream (#976).
/// </summary>
public interface ISignalDeliveryGrain : IGrainWithStringKey
{
}
