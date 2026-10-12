# Orleans streaming

## Providers

Four stream providers, selected via `FLEANS_STREAMING_PROVIDER`:

- **Redis** (default, multi-silo-safe). Reuses the existing `orleans-redis` container — the engine already provisions Redis for clustering + `PubSubStore`, so durable streaming costs zero extra infrastructure. Uses the third-party [`Universley.OrleansContrib.StreamsProvider.Redis`](https://github.com/MichaelSL/Universley.OrleansContrib.StreamsProvider.Redis) package (MIT, Orleans 10.x-compatible). The version is **pinned** in `Fleans.ServiceDefaults.csproj` — bump deliberately, since the package uses date-based versioning (`YYYY.M.D`) rather than SemVer.
- **Memory** (`FLEANS_STREAMING_PROVIDER=Memory`) — in-process only, single-silo debug-only.
- **Kafka** (`FLEANS_STREAMING_PROVIDER=Kafka`) — separate Kafka cluster.
- **AzureQueue** (`FLEANS_STREAMING_PROVIDER=AzureQueue`) — Azurite / Azure Storage.

**Production operational caveat:** Redis streaming requires Redis persistence (AOF or RDB) enabled, otherwise in-flight messages are lost on Redis restart (same concern operators already have for `PubSubStore`).

If the third-party Redis-streaming package ever goes unmaintained, the swap-out path is to fork it or build a custom adapter (~530 LoC mirroring `Fleans.Streaming.Kafka`).

## Kafka integration tests

The broker-backed tests in `Fleans.Application.Tests` (`KafkaStreamProviderIntegrationTests`, `KafkaTopicEnsureTests`) are env-gated so the default `dotnet test` stays container-free:

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
| `DeliveryRetryWindow` | `null` (Orleans default: 1 min) | Mapped onto `StreamPullingAgentOptions.MaxEventDeliveryTime` by `AddKafkaStreams`. How long Orleans keeps retrying one record for a failing subscriber before giving up. The record blocks that subscriber's cursor while it retries. |

**Capture point: subscriber keeps failing** (`x-fleans-failure-reason: delivery-failed`). Orleans retries delivery itself until `MaxEventDeliveryTime`, then calls `IStreamFailureHandler.OnDeliveryFailure` and moves the cursor past the record. `KafkaDlqFailureHandler` opens a short-lived assign-only consumer, re-reads the raw record by partition and offset (30 s timeout — a cold client pays connect, TLS and SASL/OAuth first), and publishes it with the **adapter's own producer**. The consumer is built by `KafkaClientConfigExtensions.BuildConsumer`, shared with the receiver, so security knobs and the error handler can't drift between the two. `OnSubscriptionFailure` is for subscription *initialisation* errors and is log-only.

**Undeserializable records are log-only, by decision.** `KafkaQueueAdapterReceiver` logs EventId `11107` and skips the record. Publishing from the poll loop would block the whole queue on every DLQ produce.

Every DLQ record carries `x-fleans-original-topic`, `-partition`, `-offset`, `x-fleans-failure-reason`, `x-fleans-failure-time-utc`, `x-fleans-stream-id` and `x-fleans-subscription-id`.

**Keep it simple: no owned Kafka clients.** Failures are rare, so the handler builds a consumer per failure and borrows the adapter's producer. No shared consumer, locks, leases, `Lazy` or dispose hooks. Three review rounds of shutdown machinery were removed for this; don't reintroduce a long-lived DLQ client without a measured need.

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

## Per-provider parallelism knobs

- **Redis:** `Fleans:Streaming:Redis:TotalQueueCount` (default `8`, configurable — invalid values throw `ArgumentException` at startup via `FleanStreamingExtensions.ReadRedisTotalQueueCount`).
- **Kafka:** `Fleans:Streaming:Kafka:QueueCount` (default `8` as of PR #567 — was `1`; behavior change for Kafka deployments that never set the value explicitly). Kafka also exposes `NumPartitions` as an **independent per-topic Kafka-topology knob** (default `1`); it does NOT multiply Orleans consumer parallelism because `KafkaQueueAdapterReceiver` uses consumer-group `Subscribe` mode (one consumer per topic regardless of partition count). Forward-only: `kafka-topics --alter --partitions N` can grow but not shrink.
- **AzureQueue:** `QueueNames` list length (default `8` entries, no scalar count — tuning above OR below 8 requires populating `Fleans__Streaming__AzureQueue__QueueNames__0..N` by hand).

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
Streaming__Kafka__SecurityProtocol=Ssl
Streaming__Kafka__SslCaLocation=/etc/kafka/certs/ca.pem
Streaming__Kafka__SslCertificateLocation=/etc/kafka/certs/client.pem
Streaming__Kafka__SslKeyLocation=/etc/kafka/certs/client.key
Streaming__Kafka__SslKeyPassword=s3cret
```

## AWS MSK IAM authentication

The optional package `Fleans.Streaming.Kafka.AwsMsk` (in-repo, NuGet publish tracked separately) provides one-line registration for AWS MSK clusters that use IAM auth over the standard OAUTHBEARER SASL mechanism:

```csharp
builder.AddKafkaStreamingWithMskIam("kafka", configuration, "eu-west-1");
```

IAM credentials come from the AWS workload identity chain (EC2 instance profile, EKS Pod Identity / IRSA, ECS task role, AWS SSO) — no static secrets go in configuration.

**Broker endpoint port.** MSK IAM uses port `:9098`, not `:9092` (plaintext) or `:9094` (TLS-only). Set `Fleans:Streaming:Kafka:Brokers=b-1.cluster.kafka.eu-west-1.amazonaws.com:9098`. If you see persistent SASL handshake failures despite valid IAM permissions, verify the broker port is `:9098` — `:9092` and `:9094` produce the same generic "could not get OAUTH token" error.

**Region resolution.** The `region` parameter takes precedence over `AWS_REGION` env var, which takes precedence over `AWS_DEFAULT_REGION`. Providing no region and neither env var throws `InvalidOperationException` at registration time.

**Observable signals.** EventId 11200 (`LogTokenRefreshFailed`, ERROR) fires when a SigV4 STS call fails — the most common cause is a missing IAM policy action (`kafka-cluster:Connect`, `kafka-cluster:WriteData`, `kafka-cluster:ReadData`). EventId 11201 (`LogSetTokenFailureThrew`, WARNING) indicates a disposed-client race during shutdown; it is not actionable in isolation.
