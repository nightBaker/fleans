namespace Fleans.Application.Grains;

/// <summary>
/// Addressing of the signal buckets: a subscription lives in the bucket of its workflow
/// instance, and a broadcast reaches every bucket of the name in parallel.
/// </summary>
public static class SignalCorrelationGrainFactoryExtensions
{
    /// <summary>The bucket grain that holds <paramref name="workflowInstanceId"/>'s subscriptions to <paramref name="signalName"/>.</summary>
    public static ISignalCorrelationGrain GetSignalCorrelationGrain(
        this IGrainFactory grainFactory, string signalName, Guid workflowInstanceId, SignalCorrelationOptions options)
        => grainFactory.GetGrain<ISignalCorrelationGrain>(options.BucketFor(workflowInstanceId), signalName);

    /// <summary>
    /// Broadcasts <paramref name="signalName"/> to all <see cref="SignalCorrelationOptions.BucketCount"/>
    /// buckets in parallel. A bucket that fails does not stop the others: its failure is counted
    /// in the result, and the delivered count covers the buckets that answered.
    /// </summary>
    public static async Task<SignalBroadcastResult> BroadcastSignal(
        this IGrainFactory grainFactory, string signalName, SignalCorrelationOptions options)
    {
        var buckets = Enumerable.Range(0, options.BucketCount).Select(async bucket =>
        {
            try
            {
                var delivered = await grainFactory.GetGrain<ISignalCorrelationGrain>(bucket, signalName).BroadcastSignal();
                return (Delivered: delivered, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Delivered: 0, Error: ex);
            }
        });

        var results = await Task.WhenAll(buckets);
        var errors = results.Where(r => r.Error is not null).Select(r => r.Error!).ToList();
        return new SignalBroadcastResult(
            results.Sum(r => r.Delivered),
            errors.Count,
            errors.Count == 0 ? null : new AggregateException(errors));
    }
}

/// <summary>Outcome of a broadcast over every bucket of a signal name.</summary>
/// <param name="DeliveredCount">Subscribers delivered to, summed over the buckets that answered.</param>
/// <param name="FailedBucketCount">Buckets whose broadcast call threw.</param>
/// <param name="Failure">The failures of those buckets, or null when none failed.</param>
public sealed record SignalBroadcastResult(int DeliveredCount, int FailedBucketCount, AggregateException? Failure);
