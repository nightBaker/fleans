using Orleans;
using Orleans.Runtime;

namespace Fleans.Application.Effects;

/// <summary>
/// Caller-side retry for the at-least-once sibling-grain RPCs that back the pending-events
/// durability contract (#657). The grain side dedups by <c>operationId</c> via its
/// <c>AppliedOperations</c> ledger, so retrying a transient failure is safe — a duplicate
/// op-id short-circuits to the persisted outcome instead of double-applying.
/// </summary>
public static class GrainFactoryRetryExtensions
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Builds the deterministic op-id for a child-escalation operation. The format lives in
    /// the application layer only (the domain effect carries a raw Guid) per the domain-purity
    /// rule. The id is stable per originating throw, so retries and re-escalation hops dedup.
    /// </summary>
    public static string ChildEscalationOpId(Guid escalationInstanceId)
        => $"child-escalation:{escalationInstanceId}";

    /// <summary>Builds the deterministic op-id for a child-completed operation.</summary>
    public static string ChildCompletedOpId(Guid childWorkflowInstanceId, string parentActivityId)
        => $"child-completed:{childWorkflowInstanceId}:{parentActivityId}";

    /// <summary>Builds the deterministic op-id for a child-failed operation.</summary>
    public static string ChildFailedOpId(Guid childWorkflowInstanceId, string parentActivityId)
        => $"child-failed:{childWorkflowInstanceId}:{parentActivityId}";

    /// <summary>
    /// Invokes a void grain operation, retrying on transient <see cref="OrleansException"/>
    /// with exponential backoff. <paramref name="operationId"/> identifies the logical call
    /// for retry logging; the grain method itself dedups on the same id.
    /// </summary>
    public static async Task CallWithRetry<TGrain>(
        this IGrainFactory factory,
        Guid grainKey,
        string operationId,
        Func<TGrain, Task> call,
        int maxAttempts = 5)
        where TGrain : IGrainWithGuidKey
    {
        var grain = factory.GetGrain<TGrain>(grainKey);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await call(grain);
                return;
            }
            catch (OrleansException) when (attempt < maxAttempts)
            {
                await Task.Delay(BackoffFor(attempt));
            }
        }
    }

    /// <summary>
    /// Value-returning overload (used by the escalation path). Retries on transient
    /// <see cref="OrleansException"/>; the grain dedups by <paramref name="operationId"/> and
    /// returns the persisted result on a retried call.
    /// </summary>
    public static async Task<TResult> CallWithRetry<TGrain, TResult>(
        this IGrainFactory factory,
        Guid grainKey,
        string operationId,
        Func<TGrain, Task<TResult>> call,
        int maxAttempts = 5)
        where TGrain : IGrainWithGuidKey
    {
        var grain = factory.GetGrain<TGrain>(grainKey);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call(grain);
            }
            catch (OrleansException) when (attempt < maxAttempts)
            {
                await Task.Delay(BackoffFor(attempt));
            }
        }
    }

    private static TimeSpan BackoffFor(int attempt)
        => TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
}
