using Fleans.Domain.Events;

namespace Fleans.Application.Grains;

/// <summary>
/// One bucket of a signal name's subscriptions: integer key = bucket
/// (<see cref="SignalCorrelationOptions.BucketFor(Guid)"/> of the workflow instance id),
/// key extension = signal name. Address it through
/// <see cref="SignalCorrelationGrainFactoryExtensions"/>, never with a hand-picked bucket.
/// </summary>
public interface ISignalCorrelationGrain : IGrainWithIntegerCompoundKey
{
    ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId);
    ValueTask Unsubscribe(Guid workflowInstanceId, string activityId);

    /// <summary>Delivers to every subscriber of this bucket; returns how many were delivered.</summary>
    ValueTask<int> BroadcastSignal();

    /// <summary>
    /// Puts back the subscriptions of streamed deliveries that failed transiently, so they receive
    /// the next broadcast (#742, #976). A key subscribed again in the meantime keeps its new row.
    /// </summary>
    ValueTask RestoreSubscriptions(List<SignalDeliveryItem> items);
}
