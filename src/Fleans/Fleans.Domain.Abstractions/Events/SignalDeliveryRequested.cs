namespace Fleans.Domain.Events;

/// <summary>
/// Published by a signal bucket whose broadcast goes through the stream (#976): deliver the signal
/// to subscription <paramref name="ActivityId"/> / <paramref name="HostActivityInstanceId"/> of
/// workflow instance <paramref name="WorkflowInstanceId"/>. <paramref name="OperationId"/> is fixed
/// when the bucket takes the subscription, so the instance applies a redelivered event once.
/// </summary>
[GenerateSerializer]
public sealed record SignalDeliveryRequested(
    [property: Id(0)] string SignalName,
    [property: Id(1)] Guid WorkflowInstanceId,
    [property: Id(2)] string ActivityId,
    [property: Id(3)] Guid HostActivityInstanceId,
    [property: Id(4)] string OperationId);
