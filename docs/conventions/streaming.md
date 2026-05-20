# Orleans streaming

## Providers

Four stream providers, selected via `FLEANS_STREAMING_PROVIDER`:

- **Redis** (default, multi-silo-safe). Reuses the existing `orleans-redis` container — the engine already provisions Redis for clustering + `PubSubStore`, so durable streaming costs zero extra infrastructure. Uses the third-party [`Universley.OrleansContrib.StreamsProvider.Redis`](https://github.com/MichaelSL/Universley.OrleansContrib.StreamsProvider.Redis) package (MIT, Orleans 10.x-compatible). The version is **pinned** in `Fleans.ServiceDefaults.csproj` — bump deliberately, since the package uses date-based versioning (`YYYY.M.D`) rather than SemVer.
- **Memory** (`FLEANS_STREAMING_PROVIDER=Memory`) — in-process only, single-silo debug-only.
- **Kafka** (`FLEANS_STREAMING_PROVIDER=Kafka`) — separate Kafka cluster.
- **AzureQueue** (`FLEANS_STREAMING_PROVIDER=AzureQueue`) — Azurite / Azure Storage.

**Production operational caveat:** Redis streaming requires Redis persistence (AOF or RDB) enabled, otherwise in-flight messages are lost on Redis restart (same concern operators already have for `PubSubStore`).

If the third-party Redis-streaming package ever goes unmaintained, the swap-out path is to fork it or build a custom adapter (~530 LoC mirroring `Fleans.Streaming.Kafka`).

## Per-provider parallelism knobs

- **Redis:** `Fleans:Streaming:Redis:TotalQueueCount` (default `8`, configurable — invalid values throw `ArgumentException` at startup via `FleanStreamingExtensions.ReadRedisTotalQueueCount`).
- **Kafka:** `Fleans:Streaming:Kafka:QueueCount` (default `8` as of PR #567 — was `1`; behavior change for Kafka deployments that never set the value explicitly). Kafka also exposes `NumPartitions` as an **independent per-topic Kafka-topology knob** (default `1`); it does NOT multiply Orleans consumer parallelism because `KafkaQueueAdapterReceiver` uses consumer-group `Subscribe` mode (one consumer per topic regardless of partition count). Forward-only: `kafka-topics --alter --partitions N` can grow but not shrink.
- **AzureQueue:** `QueueNames` list length (default `8` entries, no scalar count — tuning above OR below 8 requires populating `Fleans__Streaming__AzureQueue__QueueNames__0..N` by hand).

**Bumping the count rehashes Stream IDs across queues.** Consistent with the project's pre-v1 stance, **expect in-flight workflow stalls across the bump window — no formal drain procedure is shipped.**

See `website/src/content/docs/reference/streaming.md#tuning-throughput` for the sizing heuristic and the per-provider operator notes.

## Stream-id sharding by `WorkflowInstanceId`

`WorkflowEventsPublisher` keys each engine-event stream by `event.WorkflowInstanceId.ToString("D")` for:

- `ExecuteCustomTaskEvent`
- `ExecuteScriptEvent`
- `EvaluateConditionEvent`
- `EvaluateActivationConditionEvent`
- `EvaluateCompletionConditionEvent`

Per-instance FIFO ordering is preserved (Orleans guarantees FIFO per stream); cross-instance traffic distributes across `TotalQueueCount` Redis queues and across silos. Scaling `fleans-worker` replicas now provides real parallelism across distinct workflow instances for `CustomTaskHandlerBase` plugin handlers.

The default `IDomainEvent` switch branch in `Publish` logs at warning level (`LogUnknownEventType`, EventId 5001) and drops — **adding a new engine event type requires a new `switch` case.**

## Subscriber-side stream-id trap

Implicit-subscription handler grains MUST reconstruct the stream id from `this.GetPrimaryKeyString()` inside `OnActivateAsync`. Hard-coding `nameof(<Event>)` (the old, pre-sharding key) silently breaks dispatch because the grain ends up listening on a different stream than the one Orleans activated it for.

Handlers that follow this pattern (replicate it for any new `[ImplicitStreamSubscription]` grain):

- `CustomTaskHandlerBase`
- `WorkflowExecuteScriptEventHandler`
- `WorkflowEvaluateActivationConditionEventHandler`
- `WorkflowEvaluateConditionEventHandler`
- `WorkflowEvaluateCompletionConditionEventHandler`

The fallback `else { stream.SubscribeAsync(...) }` branch is intentionally absent — implicit-subscription dispatch always materialises a handle. Regressions surface in the broader script/condition/custom-task integration tests, all of which flow through these handlers.

## Custom-task per-type stream namespace

`WorkflowEventsPublisher` partitions the `ExecuteCustomTaskEvent` namespace by `TaskType` via `WorkflowEventStreams.GetExecuteCustomTaskNamespace(taskType)` → `events.ExecuteCustomTaskEvent.{taskType}`.

Each plugin's grain class only sees its own traffic (eliminates the N-plugins × M-events filter-after-deliver fanout). Plugin subclasses MUST carry `[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<their-task-type>")]` as a **literal string** (attribute arguments must be compile-time constants) — `CustomTaskHandlerBase` no longer carries a class-level attribute because Orleans only walks the concrete grain class.

`AddCustomTaskPlugin<T>(taskType, …)` validates at silo startup that (a) no other plugin already claims `taskType` and (b) `typeof(T)` declares a matching `[ImplicitStreamSubscription]`. Both throw `InvalidOperationException` with explicit messages.

The `TaskType` mismatch branch in `OnNextAsync` is unreachable under correct routing and now logs `LogTaskTypeMismatch` (EventId 4037) as a warning instead of dropping silently.
