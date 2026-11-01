namespace Fleans.Application.Grains;

/// <summary>
/// Delivers the signals a large bucket handed to the stream to one workflow instance, keyed by the
/// instance id ("D" format). It is driven only by its
/// <see cref="Fleans.Domain.Events.SignalDeliveryRequested"/> stream (#976).
/// </summary>
public interface ISignalDeliveryGrain : IGrainWithStringKey
{
}
