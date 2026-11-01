namespace Fleans.Domain.Events;

/// <summary>
/// Published by a signal bucket whose broadcast goes through the stream (#976): deliver signal
/// <paramref name="SignalName"/> to <paramref name="Items"/>, subscriptions of bucket
/// <paramref name="Bucket"/>. One stream per bucket, so the bucket's consumer delivers its pages one
/// after another and the load on the instances stays bounded.
/// </summary>
[GenerateSerializer]
public sealed record SignalDeliveryPage(
    [property: Id(0)] string SignalName,
    [property: Id(1)] int Bucket,
    [property: Id(2)] List<SignalDeliveryItem> Items);

/// <summary>
/// One delivery of a <see cref="SignalDeliveryPage"/>. <paramref name="OperationId"/> is fixed when
/// the bucket takes the subscription, so the instance applies the item of a redelivered page once.
/// </summary>
[GenerateSerializer]
public sealed record SignalDeliveryItem(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] Guid HostActivityInstanceId,
    [property: Id(3)] string OperationId);
