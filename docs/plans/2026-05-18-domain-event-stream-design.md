# Domain-event stream sharding — the default `IDomainEvent` decision

**Status:** design captured retrospectively as of commit `8117bc1` (v0.x). Decision frozen; revisit only if a new subscriber category emerges that genuinely needs cross-instance fan-out, in which case extend the publisher switch rather than reintroducing a default-stream fallback.

**Refs:** #565 (publisher-side sharding), #590 (subscriber-side resume), #592 (this design note).

## Background

Issue #565 sharded the five engine event streams by `WorkflowInstanceId.ToString("D")`:

| Event type | Stream namespace | Shard key |
|---|---|---|
| `EvaluateConditionEvent` | `events.EvaluateConditionEvent` | `WorkflowInstanceId.ToString("D")` |
| `EvaluateActivationConditionEvent` | `events.EvaluateActivationConditionEvent` | `WorkflowInstanceId.ToString("D")` |
| `ExecuteScriptEvent` | `events.ExecuteScriptEvent` | `WorkflowInstanceId.ToString("D")` |
| `ExecuteCustomTaskEvent` | `events.ExecuteCustomTaskEvent.{TaskType}` | `WorkflowInstanceId.ToString("D")` |
| `EvaluateCompletionConditionEvent` | `events.EvaluateCompletionConditionEvent` | `WorkflowInstanceId.ToString("D")` |

The original `WorkflowEventsPublisher` had a default branch — *"if `IDomainEvent` doesn't match a known type, fan it out to the shared `events` namespace as a catch-all."* #565 had to decide what to do with that catch-all under the new sharding model. This doc captures the choice and why.

## The decision: drop and log at WARNING

The default branch in `WorkflowEventsPublisher.Publish` (`src/Fleans/Fleans.Application/Events/WorkflowEventsPublisher.cs:80-85`) was rewritten to:

```csharp
default:
    // No subscriber exists for the default stream; logging and dropping surfaces
    // unhandled event types at the publisher rather than fanning them out to a
    // dead-letter stream.
    LogUnknownEventType(domainEvent.GetType().FullName ?? "(null)");
    break;
```

`LogUnknownEventType` is a `[LoggerMessage]` partial method emitting `EventId 5001` at `Warning` level with the unhandled event type's full name.

## Alternatives considered

### A. Shard the default fallback by `WorkflowInstanceId`

**Rejected.** No subscriber existed on the bare `events` namespace at the time #565 shipped (verified: `grep -r 'ImplicitStreamSubscription("events")'` against the entire repo returned zero matches). A shard key with no consumer wastes Orleans stream-provider plumbing (queue allocations, PubSubStore entries) for events that go nowhere. Sharding would also lock the engine into the assumption that every future `IDomainEvent` has a `WorkflowInstanceId` field — true for the current set but not a guaranteed contract on the marker interface.

### B. Route to a dead-letter stream

**Rejected.** A dead-letter stream provides false comfort: it silently retains messages without retention or replay semantics, and operators rarely consult it. The same forensic value (knowing an unhandled event was published) is achievable at lower cost via the WARNING log line, which surfaces in normal log aggregation pipelines (`docker compose logs fleans-core | grep "unhandled event type"`).

### C. Throw on unknown event types

**Rejected.** Throwing at the publish call site couples the runtime to "every known event type must have a switch case" — a maintenance hazard during refactors that move event types between assemblies or rename them. Drop-and-log is forgiving for code-in-flight without losing diagnostic signal.

### D. Drop and log at WARNING (chosen)

- **Zero runtime cost** for unhandled events beyond a single log call.
- **Surfaces the failure** at the publisher's own call site, with the exact event-type full name. Developers adding a new event type see the warning in their dev logs the moment they call `Publish` without a corresponding case.
- **Forces explicit intent** — the warning message itself reads *"WorkflowEventsPublisher received unhandled event type {EventTypeName}; dropping. Add a case to Publish if intentional."* This is the message defined on `LogUnknownEventType` (EventId 5001).
- **No streams allocated** for events that have no subscriber.

## Why the original #592 questions are moot

The issue body raised three open questions assuming a default stream WOULD exist:

1. *"Shard key for the default stream — `WorkflowInstanceId` or something else?"*
2. *"Subscriber pattern — are existing projections per-instance safe?"*
3. *"Audit grain identity — which subscribers need auditing?"*

Under the dropped-fallback design, all three are vacuous:

1. **No stream → no shard key.**
2. **No subscribers.** Verified: `grep -r 'ImplicitStreamSubscription("events")'` against the entire repo at commit `8117bc1` and at HEAD returns zero matches.
3. **`EfCoreWorkflowStateProjection`** — cited in the original issue as a potential subscriber — does not subscribe to streams. It is a state-based projection invoked directly from `EfCoreEventStore` (registered as singleton at `Fleans.Persistence/DependencyInjection.cs:75`). Stream sharding is structurally irrelevant to it.

## Adding a new engine event type — the 5-step recipe

When a future change requires a new `IDomainEvent` to traverse the streaming pipeline:

1. **Add a namespace constant** to `Fleans.Application.Abstractions/Events/WorkflowEventStreams.cs`:
   ```csharp
   public const string MyNewEventStreamNamespace = "events.MyNewEvent";
   ```
2. **Add a switch case** to `WorkflowEventsPublisher.Publish` that:
   - Builds `StreamId.Create(MyNewEventStreamNamespace, evt.WorkflowInstanceId.ToString("D"))` (or another shard key if the event genuinely needs different ordering).
   - Calls `_streamProvider.GetStream<MyNewEvent>(streamId).OnNextAsync(evt)`.
3. **Add a handler grain** implementing `IAsyncObserver<MyNewEvent>` + `IGrainWithStringKey`, decorated with `[ImplicitStreamSubscription(WorkflowEventStreams.MyNewEventStreamNamespace)]`.
4. **Inside `OnActivateAsync`**, rebuild the stream id from the grain's primary key — **NOT** from `nameof(...)` or a literal — per the *"Subscriber-side stream-id trap"* in [`docs/conventions/streaming.md`](../conventions/streaming.md#subscriber-side-stream-id-trap). Example:
   ```csharp
   var streamId = StreamId.Create(WorkflowEventStreams.MyNewEventStreamNamespace, this.GetPrimaryKeyString());
   var stream = streamProvider.GetStream<MyNewEvent>(streamId);
   foreach (var handle in await stream.GetAllSubscriptionHandles())
       await handle.ResumeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);
   ```
5. **Add a manual regression step** to `tests/manual/44-stream-sharding-parallelism/test-plan.md` exercising the new event type's dispatch — at minimum a two-silo distribution check (≥2 distinct silo names in the activation log).

## Invariants (cross-references)

This doc is *design intent*. The runtime invariants live in [`docs/conventions/streaming.md`](../conventions/streaming.md) and must stay in sync:

- **Stream-id sharding by `WorkflowInstanceId`** — *"The default `IDomainEvent` switch branch in `Publish` logs at warning level (`LogUnknownEventType`, EventId 5001) and drops — adding a new engine event type requires a new `switch` case."*
- **Subscriber-side stream-id trap** — *"Implicit-subscription handler grains MUST reconstruct the stream id from `this.GetPrimaryKeyString()` inside `OnActivateAsync` — hard-coding `nameof(<Event>)` (the old, pre-sharding key) silently breaks dispatch."*
- **Custom-task per-type stream namespace** — explains why `ExecuteCustomTaskEvent` is the only one with a per-`TaskType` namespace; other event types share the `events.{EventName}` pattern.
