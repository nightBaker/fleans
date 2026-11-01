# Orleans streaming

## Providers

Four stream providers, selected via `FLEANS_STREAMING_PROVIDER`:

- **Redis** (default, multi-silo-safe). Reuses the existing `orleans-redis` container — the engine already provisions Redis for clustering, so durable streaming costs zero extra infrastructure. Uses the third-party [`Universley.OrleansContrib.StreamsProvider.Redis`](https://github.com/MichaelSL/Universley.OrleansContrib.StreamsProvider.Redis) package (MIT, Orleans 10.x-compatible). The version is **pinned** in `Fleans.ServiceDefaults.csproj` — bump deliberately, since the package uses date-based versioning (`YYYY.M.D`) rather than SemVer.
- **Memory** (`FLEANS_STREAMING_PROVIDER=Memory`) — in-process only, single-silo debug-only.
- **Kafka** (`FLEANS_STREAMING_PROVIDER=Kafka`) — separate Kafka cluster.
- **AzureQueue** (`FLEANS_STREAMING_PROVIDER=AzureQueue`) — Azurite / Azure Storage.

**Production operational caveat:** Redis streaming requires Redis persistence (AOF or RDB) enabled, otherwise in-flight messages are lost on Redis restart .

If the third-party Redis-streaming package ever goes unmaintained, the swap-out path is to fork it or build a custom adapter (~530 LoC mirroring `Fleans.Streaming.Kafka`).

## Implicit subscriptions only (no `PubSubStore`)

Every provider registers with `StreamPubSubType.ImplicitOnly` (`FleanStreamingExtensions`, `KafkaSiloBuilderExtensions.AddKafkaStreams`, `AzureQueueSiloBuilderExtensions`, `Fleans.E2E.PluginHost`; #911). All Fleans subscribers are `[ImplicitStreamSubscription]` grains that resume through `GetAllSubscriptionHandles` + `ResumeAsync`, so Orleans needs no `pubsubrendezvous` grain and no `PubSubStore` grain storage. Before #911 the rendezvous state cost one Redis key per stream per instance and was never cleaned up (82 818 keys after 27 606 instances).

**Traps:**

- **An explicit `SubscribeAsync` receives nothing** on a production silo. Add a new subscriber as an `[ImplicitStreamSubscription]` grain. Tests that observe a stream from the client keep explicit pub-sub on their own test silos with a memory `PubSubStore`: `WorkflowTestBase` (used by `CustomTaskRoutingTests`) registers plain `AddMemoryStreams`, and the Kafka integration tests set `ExplicitGrainBasedAndImplicit` back after `AddKafkaStreams` / `AddFleanStreaming`.
- **Every silo of a cluster must use the same pub-sub type**, plugin hosts included. A host that still uses the Orleans default (`ExplicitGrainBasedAndImplicit`) activates rendezvous grains that need a `PubSubStore` the engine silos no longer register. Plugin hosts that call `AddFleanStreaming` get the right type from the Fleans package version; hosts that register the provider by hand (`fleans-custom-worker-example`, the `fleans-plugins` hosts: `AddPersistentStreams(…, RedisStreamFactory.Create, null)`) must pass `b => b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly)` and drop their own `AddRedisGrainStorage("PubSubStore", …)`. A mixed cluster breaks in both directions, so engine and plugin hosts upgrade together.

## Kafka integration tests

The broker-backed tests in `Fleans.Application.Tests` (`KafkaStreamProviderIntegrationTests`, `KafkaTopicEnsureTests`, `KafkaQueueOwnershipTests`, `KafkaDlqIntegrationTests`) are env-gated so the default `dotnet test` stays container-free:

```bash
FLEANS_KAFKA_TESTS=1 dotnet test Fleans.Application.Tests --filter "FullyQualifiedName~Kafka"   # Docker required
```

Without the variable they return `Inconclusive` (never `Failed`). CI runs them in `.github/workflows/kafka-tests.yml`, which fails if zero tests passed — the same silent-green guard as `pg-tests.yml`. A test that touches a stream through `TestCluster.Client` must also register the provider on the **client** (`IClientBuilderConfigurator`); a silo-only configurator leaves the client without a keyed `IStreamProvider` (#756).

## Kafka dead-letter queue

Opt-in via `EnableDeadLetterQueue` (default `false`, #686). Records whose subscriber keeps failing are published, raw and unchanged, to `<source-topic><DeadLetterTopicSuffix>` (default suffix `-dlq`, e.g. `fleans-0-dlq`) instead of being dropped. DLQ topics are created by the same topic-ensure step as the source topics.

| Knob | Default | Meaning |
|---|---|---|
| `EnableDeadLetterQueue` | `false` | Turns routing on and adds the `-dlq` topics to topic-ensure. |
| `DeadLetterTopicSuffix` | `"-dlq"` | Appended to each source topic. Rejected at startup (`ValidateOptions`, run both when the pulling-agent options are built and in `CreateAdapter`) when DLQ is on if it turns a DLQ name into a live queue topic — empty included — (`fleans-1` + `"0"` = `fleans-10`), which would feed dead letters back into the engine. |
| `DeliveryRetryWindow` | `null` (engine window: `Fleans:Streaming:MaxEventDeliveryTime`, 6 min) | Mapped onto `StreamPullingAgentOptions.MaxEventDeliveryTime` by `AddKafkaStreams`, overriding the engine-wide window (#850). A host calling `AddKafkaStreams` without `AddFleanStreaming`/`AddFleansPluginHost` keeps Orleans' 1 min. How long Orleans keeps retrying one record for a failing subscriber before giving up. The record blocks that subscriber's cursor while it retries. |

**Capture point: subscriber keeps failing** (`x-fleans-failure-reason: delivery-failed`). Orleans retries delivery itself until `MaxEventDeliveryTime`, then calls `IStreamFailureHandler.OnDeliveryFailure` and moves the cursor past the record. `KafkaDlqFailureHandler` opens a short-lived assign-only consumer, re-reads the raw record by partition and offset (30 s timeout — a cold client pays connect, TLS and SASL/OAuth first), and publishes it with the **adapter's own producer**. The consumer is built by `KafkaClientConfigExtensions.BuildConsumer`, shared with the receiver, so security knobs and the error handler can't drift between the two. `OnSubscriptionFailure` is for subscription *initialisation* errors and is log-only.

**Capture point: record cannot be deserialized** (`x-fleans-failure-reason: deserialization-failed`, #866). `KafkaQueueAdapterReceiver` logs `11113` and publishes the raw record from the poll loop, then moves on; with DLQ off it logs `11107` and drops it. This reverses the earlier "log-only" decision: it assumed such records were rare garbage, but #866 showed a whole event type silently lost on every queue a plugin host owned. After a publish the receiver returns the batch read so far instead of polling on: at most one DLQ publish (30 s timeout) per poll, so records already read never wait behind a run of bad ones, and nothing touches the consumer after the `await` (`Shutdown` may have disposed it meanwhile — the poll loop used to be fully synchronous). Under version skew a whole event type fails, so bad records then drain one per poll — slow, but healthy traffic keeps flowing. Both capture points share the adapter's single `KafkaDeadLetterPublisher` (`KafkaQueueAdapter.DeadLetters`, `null` when the DLQ is off). The bad record's offset is committed by the next `Commit()`; if the silo restarts before that, the record is read and dead-lettered again.

Every DLQ record keeps the source record's own headers and adds `x-fleans-original-topic`, `-partition`, `-offset`, `x-fleans-failure-reason` and `x-fleans-failure-time-utc`. `delivery-failed` records add `x-fleans-stream-id` and `x-fleans-subscription-id`; `deserialization-failed` records add `x-fleans-exception` (type and message — the stream id is inside the payload that could not be read).

**Keep it simple: no owned Kafka clients.** Delivery failures are rare, so the handler builds a consumer per failure and borrows the adapter's producer; deserialization failures need no consumer at all (the receiver already holds the record). No shared consumer, locks, leases, `Lazy` or dispose hooks. Three review rounds of shutdown machinery were removed for this; don't reintroduce a long-lived DLQ client without a measured need.

**Traps:**

- **Hook `OnDeliveryFailure`, not `OnSubscriptionFailure`.** The first DLQ draft counted retries in `OnSubscriptionFailure`, so nothing was ever dead-lettered. Orleans owns the retry loop, so there is no per-record retry *count* to configure, only the time window.
- **Without DLQ, a poison record is dropped, not replayed forever.** After the window Orleans logs and skips it, and the receiver's next commit moves past it. The DLQ exists to make that loss recoverable.
- **Re-consume needs `AutoOffsetReset=Error`.** With `Earliest`, a purged offset silently jumps to the earliest retained record, so "no longer retained" (`12004`) is never logged and the drop looks like a timeout (`12006`).
- **Sequence tokens must be built from Kafka coordinates only** (offset, partition, event index). Orleans keeps a subscriber's last token across queue rebalances and silo restarts, so any process-local field (e.g. an arrival counter) makes tokens from silo B look older than silo A's and skips records. `KafkaBatchContainer.GetEvents` copies the partition into every event token so a handler given an event token still locates the right record. Cross-partition ordering with `NumPartitions > 1` is a known limitation (#812).
- **The DLQ unit is the whole Kafka record, once per failing subscription.** A record that fails for several subscribers is dead-lettered once per subscriber (`x-fleans-subscription-id` tells them apart); a record carrying several events is dead-lettered whole, including events already delivered. Replay is safe only because of the [stream-redelivery idempotency](../../CLAUDE.md) guards; DLQ consumers dedupe on `x-fleans-original-{topic,partition,offset}`.
- **The 30 s publish timeout only bounds the wait, not the message.** The pulling agent stops waiting after 30 s (`12001`), but the shared producer keeps the message for its `message.timeout.ms` (librdkafka default 5 min) and may still deliver it. During a long DLQ outage those messages also occupy the producer queue that live engine events use. A `12001` therefore means "not confirmed", not "certainly lost".
- **The DLQ is best-effort, not lossless.** A DLQ publish that is never delivered, or a record that is no longer retained by the broker, is logged (EventIds `12001`, `12004`–`12006`) and the record is dropped. Alert on `12001`.
- **The factory needs the adapter before handlers.** `GetDeliveryFailureHandler` returns a DLQ handler only after `CreateAdapter` ran; otherwise it logs `11112` and falls back to no-op. Orleans creates the adapter first today; the log makes a lifecycle change visible.
- **The DLQ does not survive a silo crash during the retry window.** The receiver's parameterless `Commit()` can commit past a record Orleans is still retrying; if the silo dies before `OnDeliveryFailure`, the new owner starts after it and the record is neither delivered nor dead-lettered. Fixing commit semantics is #813.

Integration coverage: `KafkaDlqIntegrationTests` (env-gated like the other Kafka tests above).

## Kafka durability defaults

Three knobs on `KafkaStreamingOptions` control producer and topic durability:

| Option | Default | Purpose |
|---|---|---|
| `EnableIdempotence` | `true` | Exactly-once produce semantics (Kafka EOS Phase 1). |
| `Acks` | `All` | Leader + all in-sync replicas must acknowledge before produce returns. |
| `ReplicationFactor` | `3` | Topics created at silo startup use RF=3, surviving one broker loss. |

**`ValidateOptions` startup cross-check:** `KafkaQueueAdapterFactory.CreateAdapter()` throws `InvalidOperationException` when `EnableIdempotence=true && Acks != All`. Confluent.Kafka v2.x enforces this combination internally; the check surfaces it at silo startup rather than at first produce. A startup warning is emitted when `Acks != All` and idempotence is off — this is a valid but lower-durability configuration and should only be used in non-production workloads.

**Broker-count fallback:** `EnsureTopicsAsync` probes the live broker count via `AdminClient.GetMetadata()` before creating topics. When `brokerCount < ReplicationFactor`, the effective RF is clamped to `brokerCount`:
- Single-broker cluster → `effectiveRf=1`, logged at `Information` (expected in dev/CI).
- Multi-broker cluster with fewer brokers than RF → logged at `Warning` (indicates degraded cluster).
- Zero brokers → `InvalidOperationException` (cluster unreachable, fail-fast).

The broker-count fallback applies only to **new** topics created at startup. For **existing** topics, the stored RF is set when the topic was first created. To change the RF of an existing topic use `kafka-reassign-partitions` or recreate the topic (with a drain window).

## Pulling-agent poll period

`Fleans:Streaming:PollPeriod` (a `TimeSpan`; absent = Orleans' default `00:00:00.100`) sets `StreamPullingAgentOptions.GetQueueMsgsTimerPeriod` for whichever provider `AddFleanStreaming` registers. A stream event waits up to one period for its queue's next read — before scripts and conditions left the stream, that wait was most of a 10-script process's ~1.3 s. The remaining stream traffic is custom tasks and `InstanceAdvanced`, so a shorter period mainly lowers custom-task and read-model latency, at the cost of polling every queue more often. Non-positive or unparseable values throw at startup (`ReadPollPeriod`; `StreamPollPeriodTests`).

## Delivery window and Redis trimming (#850)

Two Orleans/provider defaults used to drop stream events without an error, leaving the instance stuck:

- **`MaxEventDeliveryTime`** — a delivery is a grain call retried until `StreamPullingAgentOptions.MaxEventDeliveryTime`, then the agent skips the event for good. Implicit subscriptions are never faulted, so nothing records the skip. Orleans' default is 1 minute, but a custom-task handler grain is keyed per instance and non-reentrant (#815): an event queued behind a 300 s `rest-call` expired and that task never ran. `Fleans:Streaming:MaxEventDeliveryTime` (`TimeSpan`, default `WorkflowEventStreams.DefaultMaxEventDeliveryTime` = 6 min) now applies to every provider `AddFleanStreaming` registers (`ReadMaxEventDeliveryTime` rejects non-positive values at startup), and `AddFleansPluginHost` gives plugin silos the same window — their pulling agents own queues too. Kafka's `DeliveryRetryWindow`, when set, still wins. Cost: a subscriber that keeps throwing blocks its queue cursor for the whole window before the event is dropped (or dead-lettered on Kafka). Covered by `StreamDeliveryDefaultsTests` and `PluginHostPlacementTests`.
- **Redis `MaxStreamLength`** — the provider trims with `XTRIM MAXLEN ~`, which deletes entries whether or not the consumer group read them. The default is now `100_000` per queue (was 1000). Size it above the largest backlog a queue can reach — a slow deploy or a stalled consumer at your peak event rate — times a safety factor; memory is roughly the entry size × length × `TotalQueueCount`. Ack-aware trimming (`XTRIM … ACKED`, Redis 8.2+) would remove the risk entirely but needs upstream provider support.

## Kafka queue ownership: no consumer-group membership

The Orleans queue balancer decides which silo reads each queue; `KafkaQueueAdapterReceiver` must not add Kafka's consumer-group balancer on top. It `Assign`s every partition of its topic at `Offset.Stored` (the group's committed offset, else `Earliest`) and commits to `ConsumerGroup` as before, but never joins the group (#954). With `Subscribe`, one consumer closed between its first `JoinGroup` and the rejoin — a joining silo drops queues ~100 ms after taking them — left a pending member that held **every queue on every silo** in a rebalance until the broker's `session.timeout.ms` (45 s); custom tasks on a fresh split cluster stalled that long. `KafkaQueueOwnershipTests` fails if a receiver ever shows up as a group member.

- **One partition lookup at a time.** Orleans stops waiting on `Initialize` after `InitQueueTimeout` and starts pulling, so a slow lookup can still be running when the first `GetQueueMessagesAsync` arrives. Both await the same in-flight `Task` (`_sync`): a second `Assign` would move the position back to the committed offset and redeliver records already read. `Shutdown` waits for it too before disposing the consumer whose handle the lookup uses.
- **Failures never throw.** A failed lookup (broker down, topic missing) logs EventId 11116 and is retried with its own backoff, 1 s doubling to 1 min. Throwing would make Orleans log an error on every pull.
- **A failed `Consume` drops the assignment.** Without group membership nothing re-runs a failed committed-offset fetch: on a request-level OffsetFetch error (`Not coordinator` while a fresh broker loads the group) librdkafka 2.6.1 leaves the partition in its `queried` list — never fetching, no retry — until it is unassigned (`rdkafka_assignment.c`, `apply_offsets` ignores an empty reply). With `Subscribe` the next rebalance cleared it. So on any `ConsumeException` the receiver logs EventId 11109, `IncrementalUnassign`s its partitions and assigns them again after the lookup backoff; records read since the last commit are read again. Seen in CI as a custom task that never reached the plugin host (#969). Test: `KafkaQueueAdapterReceiverTests`.
- **Added partitions.** Once assigned, the receiver looks its topic up again every minute (`KafkaStreamingOptions.PartitionCheckInterval`, internal so tests can shorten it) and `IncrementalAssign`s new partitions at `Offset.Stored`, so `kafka-topics --alter --partitions` needs no restart. Partitions already being read keep their position.
- **Handoff overlap.** Kafka no longer guarantees one reader per partition. While a queue moves between silos, the old and new owners can both read it from the committed offset until the old one learns of the move, so a few records are delivered twice and an offset commit can move back a little (more replay at the next handoff). This is the same at-least-once contract as a crash before commit; the stream-redelivery idempotency invariant covers it.
- **Upgrade: no mixed rollout.** Old (`Subscribe`) and new receivers must not share a consumer group: the broker rejects the new receivers' commits while the group has members, and both read the same partitions. Stop every silo before starting the new version (the chart's default rolling update would run both together, so scale to zero first). A fresh `ConsumerGroup` is no way around it: with no committed offsets, it reads every topic again from `Earliest`.

## Kafka consumer lag

`KafkaQueueAdapterReceiver` reports `fleans.kafka.consumer.lag` (gauge, records, tags `topic` + `partition`) in the `Fleans` meter after each poll that returned records: the consumer's **cached** high watermark (`GetWatermarkOffsets`, no broker call) minus the last offset read, plus one (`KafkaConsumerLag`). The Kafka package does not reference `Fleans.Application.Abstractions`, so it creates its own `Meter("Fleans")` — OpenTelemetry matches meters by name. It measures read lag, not committed-offset lag (commits follow delivery within the same pulling-agent cycle). No max-across-partitions gauge or lag health check by design (#687): take `max()` in PromQL, alert in Prometheus, scale with KEDA — a lag-driven health check would pull loaded pods out of rotation exactly when they're needed.

## Per-provider parallelism knobs

- **Redis:** `Fleans:Streaming:Redis:TotalQueueCount` (default `8`, configurable — invalid values throw `ArgumentException` at startup via `FleanStreamingExtensions.ReadRedisTotalQueueCount`).
- **Kafka:** `Fleans:Streaming:Kafka:QueueCount` (default `8` as of PR #567 — was `1`; behavior change for Kafka deployments that never set the value explicitly). Kafka also exposes `NumPartitions` as an **independent per-topic Kafka-topology knob** (default `1`); it does NOT multiply Orleans consumer parallelism because `KafkaQueueAdapterReceiver` reads every partition of its topic with one consumer. Forward-only: `kafka-topics --alter --partitions N` can grow but not shrink; receivers start reading added partitions within a minute.
- **AzureQueue:** `QueueNames` list length (default `8` entries, no scalar count — tuning above OR below 8 requires populating `Fleans__Streaming__AzureQueue__QueueNames__0..N` by hand).

**Aspire:** set `FLEANS_STREAMING_REDIS_TOTAL_QUEUE_COUNT` or `FLEANS_STREAMING_KAFKA_QUEUE_COUNT` on the AppHost (both default to `8`). `WithStreaming` forwards the selected provider's value to Core, Worker and any dev Plugin Host. For example, run `FLEANS_STREAMING_REDIS_TOTAL_QUEUE_COUNT=16 dotnet run --project Fleans.Aspire` from `src/Fleans/`. These settings also apply at `aspire publish` time and are written into the generated Compose/Kubernetes environment; regenerate the output when changing them. Helm exposes the equivalent `streaming.redis.totalQueueCount` / `streaming.kafka.queueCount` values.

**Same queue count on every silo.** Each silo builds the stream hash ring from its own local config, and Orleans never compares the value across silos. If silos disagree, they map stream IDs to different queues and streams silently misroute — no error, no log. Keep the knob a fixed config value shipped identically to every silo (same env / Helm values); never derive it from host properties such as the core count (#699).

**Bumping the count rehashes Stream IDs across queues.** Consistent with the project's pre-v1 stance, **expect in-flight workflow stalls across the bump window — no formal drain procedure is shipped.**

See `website/src/content/docs/reference/streaming.md#tuning-throughput` for the sizing heuristic and the per-provider operator notes.

## Kafka production preset

`WithProductionDefaults(name)` is an **opt-in**, chainable extension that sizes `NumPartitions` from `Environment.ProcessorCount`:

```csharp
builder.AddKafkaStreams("kafka", configuration)
       .WithProductionDefaults("kafka");
```

Applied values:

| Property | Formula |
|---|---|
| `NumPartitions` | `max(1, Environment.ProcessorCount)` — scales broker-side write parallelism |

**The `name` argument must match exactly** the name passed to `AddKafkaStreams`. A mismatch silently applies overrides to a different named-options instance and leaves the provider at defaults — the absence of EventId 11000 in startup logs is the observable signal.

**The preset deliberately leaves `QueueCount` alone.** `QueueCount` maps to `HashRingStreamQueueMapperOptions.TotalQueueCount`, which every silo must agree on (see *Same queue count on every silo* above). Deriving it from the host's core count let a mixed-core autoscaling fleet misroute streams silently, so it stays a plain config value (#699). `NumPartitions` is a per-topic setting fixed by whichever silo creates the topic first, so differing core counts there are harmless.

**`NumPartitions` is forward-only:** `kafka-topics --alter --partitions N` can grow but not shrink. Deploying with the preset and then removing it leaves topics at the preset partition count — this is the safe direction, but plan accordingly.

## Stream-id sharding by `WorkflowInstanceId`

`WorkflowEventsPublisher` keys each engine-event stream by `event.WorkflowInstanceId.ToString("D")`. Today that is only `ExecuteCustomTaskEvent`: script tasks are dispatched straight to `ScriptExecutorGrain` and conditions are evaluated inside the instance (no stream, no pulling-agent wait — see [invariants](invariants.md#script-dispatch-and-in-process-conditions)). `ExecuteScriptEvent` stays in `Fleans.Domain.Abstractions` because it is the dispatch's grain-call argument to the worker silo.

Per-instance FIFO ordering is preserved (Orleans guarantees FIFO per stream); cross-instance traffic distributes across `TotalQueueCount` Redis queues and across silos. Scaling `fleans-worker` replicas now provides real parallelism across distinct workflow instances for `CustomTaskHandlerBase` plugin handlers. Each handler grain is keyed by workflow instance id (Orleans single-activation → one silo at a time), each queue is pulled by exactly one silo, and the `GetVariablesIfActive` pre-execution check skips at-least-once redeliveries of an activity that already finished (see [`invariants.md`](invariants.md#stream-redelivery-idempotency) guard 8). A run interrupted mid-`ExecuteAsync` (worker crash, activation deactivation) is redelivered and runs again, so plugins still need idempotent external calls.

`InstanceAdvanced` (#861) is keyed the same way (`events.InstanceAdvanced` / instance id) but is published by `WorkflowInstance` itself, straight to the stream, after a journal append — not through `WorkflowEventsPublisher`: it is not an `IDomainEvent` of the workflow, and a grain hop per append is pure overhead. Its subscriber is `WorkflowInstanceProjector` (`[WorkerPlacement]`: a `worker-` / `combined-` silo — see [placement-and-roles.md](placement-and-roles.md#read-model-projector-on-worker-silos-861); it needs the event store and the read model); see [`persistence.md`](persistence.md#event-store-journal-snapshots-read-model). Like every stream event it lives in `Fleans.Domain.Abstractions`.

The default `IDomainEvent` switch branch in `Publish` logs at warning level (`LogUnknownEventType`, EventId 5001) and drops — **adding a new engine event type requires a new `switch` case** — and that event type must live in `Fleans.Domain.Abstractions` (next paragraph), even if its subscriber is not an `IAsyncObserver<T>` grain the guard test can see.

**Stream event types live in `Fleans.Domain.Abstractions` (#866).** Every silo that registers the stream provider owns a slice of the queues — external plugin hosts included — and must deserialize every record on them before forwarding it to the subscriber's silo. A plugin host carries only `Fleans.Worker`'s dependency closure, so an event type left in `Fleans.Domain` cannot be read there: the Kafka receiver drops it (or dead-letters it) and the instance hangs with the activity still executing. Keep the namespace `Fleans.Domain.Events` when adding one. `StreamEventAssemblyTests` fails if a stream subscriber (`IAsyncObserver<T>` grain in `Fleans.Application` / `Fleans.Worker`) observes a type from any other assembly. The Redis provider happens to be immune (it keeps payloads opaque until `GetEvents<T>` on the consumer), Kafka is not; the `e2e-split-roles` CI job runs on both.

## Subscriber-side stream-id trap

Implicit-subscription handler grains MUST reconstruct the stream id from `this.GetPrimaryKeyString()` inside `OnActivateAsync`. Hard-coding `nameof(<Event>)` (the old, pre-sharding key) silently breaks dispatch because the grain ends up listening on a different stream than the one Orleans activated it for.

Handlers that follow this pattern (replicate it for any new `[ImplicitStreamSubscription]` grain):

- `CustomTaskHandlerBase`
- `WorkflowInstanceProjector` (`Fleans.Application/Projections/`)

**A subscriber keyed by workflow instance must free its activation when its work is done.** Idle collection takes 15 minutes, so a grain per instance that waits for it holds one activation per instance started in the last 15 minutes — at 200 instances/s ~180 000, which filled a 384 MB plugin host and left it in GC, completing nothing (#922). `CustomTaskHandlerBase` calls `DeactivateOnIdle()` after every handled event, `WorkflowInstanceProjector` once the instance completed.

The fallback `else { stream.SubscribeAsync(...) }` branch is intentionally absent — implicit-subscription dispatch always materialises a handle. Regressions surface in the custom-task and projection integration tests, which flow through these handlers.

## Custom-task per-type stream namespace

`WorkflowEventsPublisher` partitions the `ExecuteCustomTaskEvent` namespace by `TaskType` via `WorkflowEventStreams.GetExecuteCustomTaskNamespace(taskType)` → `events.ExecuteCustomTaskEvent.{taskType}`.

Each plugin's grain class only sees its own traffic (eliminates the N-plugins × M-events filter-after-deliver fanout). Plugin subclasses MUST carry `[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<their-task-type>")]` as a **literal string** (attribute arguments must be compile-time constants) — `CustomTaskHandlerBase` no longer carries a class-level attribute because Orleans only walks the concrete grain class.

`AddCustomTaskPlugin<T>(taskType, …)` validates at silo startup that (a) no other plugin already claims `taskType` and (b) `typeof(T)` declares a matching `[ImplicitStreamSubscription]`. Both throw `InvalidOperationException` with explicit messages. The subscription literal also gives the handler its grain type, `custom-task.<task-type>` (`CustomTaskGrainTypeProvider`), so handlers on different plugin hosts never share one because their classes share a name — see [adding-a-custom-task-plugin.md](adding-a-custom-task-plugin.md).

The `TaskType` mismatch branch in `OnNextAsync` is unreachable under correct routing and now logs `LogTaskTypeMismatch` (EventId 4037) as a warning instead of dropping silently.

**External task types skip the stream** (#979). A `TaskType` listed in `Fleans:ExternalTasks:TaskTypes` goes to `IExternalTaskQueue` (`Fleans.Application/ExternalTasks`; Kafka implementation `Fleans.Infrastructure/ExternalTasks/KafkaExternalTaskQueue`, topic `fleans.tasks.<type>`), not to `events.ExecuteCustomTaskEvent.<type>` — a plugin registered for the same type gets nothing. The publish runs inside the instance's turn, so the queue must not call back into the instance, and it must fail well inside the 30 s grain-call timeout (Kafka: `message.timeout.ms` 10 s; librdkafka retries within it). A failed hand-off — to the queue or to a plugin stream — fails the activity in the same turn (`PublishCustomTask`, log 1169), like any registration failure. There is no engine-side republish: once a record is in Kafka, redelivery is the worker's job — it commits an offset only after its REST report succeeded. A new queue provider implements `IExternalTaskQueue` and adds a case to `AddExternalTasks`.

## Kafka SASL / TLS

Kafka security is configured via nine properties on `KafkaStreamingOptions`:

| Property | Type | Default | Notes |
|---|---|---|---|
| `SecurityProtocol` | `KafkaSecurityProtocol` | `Plaintext` | Backward-compatible default; existing deployments are unaffected |
| `SaslMechanism` | `KafkaSaslMechanism?` | `null` | Required for `SaslPlaintext` / `SaslSsl` protocols |
| `SaslUsername` | `string?` | `null` | Required for PLAIN / SCRAM-SHA-256 / SCRAM-SHA-512 |
| `SaslPassword` | `string?` | `null` | Required for PLAIN / SCRAM-SHA-256 / SCRAM-SHA-512 |
| `OAuthBearerTokenProvider` | `Action<IClient, string>?` | `null` | Required for OAuthBearer; wired via `SetOAuthBearerTokenRefreshHandler` on each client builder |
| `SslCaLocation` | `string?` | `null` | Path to CA certificate (PEM). Pins the CA that signed the broker cert |
| `SslCertificateLocation` | `string?` | `null` | Path to client certificate (PEM). Required for mTLS; must be paired with `SslKeyLocation` |
| `SslKeyLocation` | `string?` | `null` | Path to client private key (PEM). Required for mTLS; must be paired with `SslCertificateLocation` |
| `SslKeyPassword` | `string?` | `null` | Passphrase for the client private key. Requires `SslKeyLocation` to be set |

**Fail-fast validation.** `KafkaClientConfigExtensions.ApplySecurity` is called on all three client builders (producer, consumer, admin) and throws `InvalidOperationException` at silo startup for any misconfigured combination — missing mechanism, empty credentials, missing OAuthBearer provider, SSL paths with incompatible protocol, unpaired cert/key, or password without key. This surfaces configuration errors before the first broker connection.

**Enum ownership.** `KafkaSecurityProtocol` and `KafkaSaslMechanism` are Fleans-owned enums (1:1 switch to Confluent types) so Confluent types stay out of the public API surface. Unknown enum values throw at startup.

**OAuthBearer handler.** The `OAuthBearerTokenProvider` callback matches Confluent.Kafka's `SetOAuthBearerTokenRefreshHandler(Action<IClient, string>)` signature directly — no adapter needed. The callback is registered after `ApplySecurity` on each builder.

### mTLS / client-certificate authentication

Two distinct SSL modes are supported:

**Server-cert validation against a private CA** (no client cert): Set `SecurityProtocol = Ssl` (or `SaslSsl`) and `SslCaLocation` only. Useful when the broker cert is issued by an internal CA not in the OS trust store. Omitting all `Ssl*` paths with `SecurityProtocol = Ssl` is also valid — the OS trust store is used for broker-cert validation, and the silo logs a WARNING at startup (EventId 11100) to confirm this is intentional.

**Mutual TLS (full mTLS)** — client presents a certificate to the broker: Set `SecurityProtocol = Ssl` and all three paths:

- `SslCaLocation` — path to the CA that issued the broker cert
- `SslCertificateLocation` — path to the client certificate
- `SslKeyLocation` — path to the client private key
- `SslKeyPassword` — passphrase (omit if the key is not passphrase-protected)

> **Path resolution:** Confluent.Kafka resolves `Ssl*Location` paths relative to the **silo's working directory**, not the .NET assembly directory. In containerised deployments (Kubernetes, Docker), prefer **absolute paths** (e.g. `/etc/kafka/certs/ca.pem`) to avoid CWD-dependent failures.

Example environment variables (full mTLS + passphrase-protected key):

```
Fleans__Streaming__Kafka__SecurityProtocol=Ssl
Fleans__Streaming__Kafka__SslCaLocation=/etc/kafka/certs/ca.pem
Fleans__Streaming__Kafka__SslCertificateLocation=/etc/kafka/certs/client.pem
Fleans__Streaming__Kafka__SslKeyLocation=/etc/kafka/certs/client.key
Fleans__Streaming__Kafka__SslKeyPassword=s3cret
```

**Helm.** `streaming.kafka.security.*` in `charts/fleans/values.yaml` sets these keys (`fleans.kafkaSecurityEnv` in `_helpers.tpl`): the SASL password comes from an existing `Secret`, and `tlsSecret` is mounted at `/etc/fleans/kafka` on every Deployment that includes `fleans.commonEnv` (`ca.crt`, plus `tls.crt` / `tls.key` with `clientCertificate: true`). `SslKeyPassword` and OAuthBearer have no chart value — the first goes through `extraEnv`, the second needs code. The Aspire AppHost always runs its own plaintext Kafka container, so it has no security passthrough.

## AWS MSK IAM authentication

The optional package `Fleans.Streaming.Kafka.AwsMsk` (in-repo, NuGet publish tracked separately) provides one-line registration for AWS MSK clusters that use IAM auth over the standard OAUTHBEARER SASL mechanism:

```csharp
builder.AddKafkaStreamingWithMskIam("kafka", configuration, "eu-west-1");
```

IAM credentials come from the AWS workload identity chain (EC2 instance profile, EKS Pod Identity / IRSA, ECS task role, AWS SSO) — no static secrets go in configuration.

**Broker endpoint port.** MSK IAM uses port `:9098`, not `:9092` (plaintext) or `:9094` (TLS-only). Set `Fleans:Streaming:Kafka:Brokers=b-1.cluster.kafka.eu-west-1.amazonaws.com:9098`. If you see persistent SASL handshake failures despite valid IAM permissions, verify the broker port is `:9098` — `:9092` and `:9094` produce the same generic "could not get OAUTH token" error.

**Region resolution.** The `region` parameter takes precedence over `AWS_REGION` env var, which takes precedence over `AWS_DEFAULT_REGION`. Providing no region and neither env var throws `InvalidOperationException` at registration time.

**Observable signals.** EventId 11200 (`LogTokenRefreshFailed`, ERROR) fires when a SigV4 STS call fails — the most common cause is a missing IAM policy action (`kafka-cluster:Connect`, `kafka-cluster:WriteData`, `kafka-cluster:ReadData`). EventId 11201 (`LogSetTokenFailureThrew`, WARNING) indicates a disposed-client race during shutdown; it is not actionable in isolation.
