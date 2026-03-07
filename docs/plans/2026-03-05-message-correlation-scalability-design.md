# Message Correlation Scalability & Reliability

## Problem

Two open HIGH-severity risks from the 2026-02-17 architectural risk audit:

**H1 — Message correlation bottleneck.** `MessageCorrelationGrain` uses the message name as the grain key. All workflows subscribing to the same message type (e.g., `"paymentReceived"`) route through a single grain. This grain holds a `List<MessageSubscription>`, does linear scans, and serializes all subscribe/unsubscribe/deliver calls through Orleans' single-threaded grain model.

**H3 — Fire-and-forget delivery.** The grain removes the subscription *before* calling `HandleMessageDelivery()`. If delivery fails (network error, grain crash), the message is lost permanently and the workflow deadlocks.

## Solution

### Grain Key: Partition by Correlation Key

Change the grain key from `messageName` to `messageName/encodedCorrelationKey`.

Each unique `(messageName, correlationKey)` pair gets its own grain. Since correlation keys are unique per subscription (duplicates already throw), each grain holds at most **one** subscription.

**Key encoding:** Use `Uri.EscapeDataString(correlationKey)` to handle correlation keys containing `/` or other special characters. A static helper centralizes this:

```csharp
public static class MessageCorrelationKey
{
    public static string Build(string messageName, string correlationKey)
        => $"{messageName}/{Uri.EscapeDataString(correlationKey)}";
}
```

**Scalability result:** Zero contention. Subscribe/deliver/unsubscribe for different correlation keys execute in parallel across different grains.

### State Simplification

**Before:**
```csharp
public class MessageCorrelationState
{
    public List<MessageSubscription> Subscriptions { get; set; } = new();
}
```

**After:**
```csharp
public class MessageCorrelationState
{
    public MessageSubscription? Subscription { get; set; }
}
```

### Delivery: Confirm-Then-Remove

**Before (at-most-once):**
1. Remove subscription from state → `WriteStateAsync()`
2. Call `HandleMessageDelivery()` — if this fails, message is lost

**After (at-least-once):**
1. Call `HandleMessageDelivery()` on the workflow instance
2. On success: clear state via `ClearStateAsync()` (deletes DB row)
3. On failure: subscription remains, exception propagates to caller → they can retry

```csharp
public async ValueTask<bool> DeliverMessage(ExpandoObject variables)
{
    if (_state.State.Subscription is null) return false;

    var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(
        _state.State.Subscription.WorkflowInstanceId);
    await workflowInstance.HandleMessageDelivery(
        _state.State.Subscription.ActivityId,
        _state.State.Subscription.HostActivityInstanceId,
        variables);

    _state.State.Subscription = null;
    await _state.ClearStateAsync();
    return true;
}
```

### Idempotency Guard

`HandleMessageDelivery()` must be idempotent to handle the crash window between successful delivery and state clear. Add an early return if the activity is no longer active:

```csharp
// In WorkflowInstance.EventHandling.cs HandleMessageDelivery():
var entry = State.GetFirstActive(activityId);
if (entry is null) return;  // already completed — idempotent no-op
```

### Interface Changes

```csharp
public interface IMessageCorrelationGrain : IGrainWithStringKey
{
    // Key: "messageName/encodedCorrelationKey"
    ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId);
    ValueTask Unsubscribe();
    ValueTask<bool> DeliverMessage(ExpandoObject variables);
}
```

Correlation key and message name removed from method signatures — embedded in grain key.

### Persistence Changes

Keep `MessageSubscription` as a **separate queryable table** (not inlined) for admin/debugging queries. The grain storage changes to:

- Each grain state row has at most one related `MessageSubscription` row
- `ClearStateAsync()` deletes both the grain state row and the subscription row
- `WriteStateAsync()` upserts the single subscription
- `DiffSubscriptions()` simplifies to insert-or-delete (no list diffing)

The `MessageSubscription` table remains queryable: `SELECT * FROM MessageSubscriptions WHERE MessageName = 'X'` still works for admin UI.

### Callers That Change

1. `WorkflowInstance.EventHandling.cs` — `RegisterMessageSubscription()`, `RegisterBoundaryMessageSubscription()`
2. `WorkflowInstance.ActivityLifecycle.cs` — event-based gateway cancellation (unsubscribe)
3. `BoundaryEventHandler.cs` — `UnsubscribeBoundaryMessageSubscriptionsAsync()`
4. `WorkflowController.cs` — message delivery endpoint
5. `EfCoreMessageCorrelationGrainStorage.cs` — persistence layer
6. All message-related tests

### Design Decisions

**Why string key with encoding over `IGrainWithGuidCompoundKey`?** Orleans has no `IGrainWithStringCompoundKey`. Using `GuidCompoundKey` would require hashing the message name to a deterministic GUID, making grain keys opaque in logs. String keys are human-readable and easier to debug in a workflow engine.

**Why not hash-partitioned?** Partitioning by correlation key gives perfect distribution (one grain per subscription). Hash partitioning (N shards) still has contention within each shard and requires tuning N.

**Why not outbox pattern?** Confirm-then-remove with idempotent receivers achieves at-least-once without the complexity of an outbox table and retry grain.

## Testing

**Existing tests to update:**
- `MessageIntermediateCatchEventTests` — update grain key construction
- `MessageBoundaryEventTests` — same
- `EfCoreMessageCorrelationGrainStorageTests` — adapt to single-subscription model

**New tests:**
1. Idempotent delivery — deliver twice on same grain key → second returns `false`
2. Delivery failure resilience — mock `HandleMessageDelivery` to throw → subscription NOT cleared, retry succeeds
3. Correlation key with special characters (`/`, `?`, `&`, unicode)
4. Concurrent subscribe on same key → throws `InvalidOperationException`
5. `ClearStateAsync` on delivery → DB row deleted (no orphaned rows)
