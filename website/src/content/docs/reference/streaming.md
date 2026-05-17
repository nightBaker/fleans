---
title: Streaming providers
description: How Fleans publishes domain events on Orleans Streams and how to switch between the in-memory and Kafka-backed providers.
---

Fleans publishes domain events (`EvaluateConditionEvent`, `EvaluateActivationConditionEvent`,
`ExecuteScriptEvent`, …) through **Orleans Streams**. The stream provider is pluggable at startup:
the default is Orleans' in-memory provider, and a Kafka-backed provider ships alongside it for
cross-silo event durability.

{/* drift-guard: KafkaStreamingOptions.cs:4-23 + grep "SecurityProtocol|Sasl|Ssl" returns 0 matches across Fleans.Streaming.Kafka/ + Fleans.Aspire/Program.cs:13-15,57; pinned at branch=docs/399-kafka-production-warning SHA=b7d80af; refresh if these line ranges or files are renamed */}

:::caution[Kafka provider is not production-ready]
The v1 `Fleans.Streaming.Kafka` adapter ships **plaintext brokers only**, defaults to
`ReplicationFactor=1`, and offers no DLQ or schema-registry integration. **Do not point
this at Confluent Cloud, Amazon MSK, Aiven, Redpanda Cloud, or any managed Kafka
service yet** — the silo will fail to connect with `Disconnected: SASL authentication required`
because no `SecurityProtocol` / SASL / TLS configuration knobs exist
(`KafkaStreamingOptions.cs` confirms the surface today).

See [§ Production-readiness gaps](#production-readiness-gaps) below for the full
matrix and tracking issue [#474](https://github.com/nightBaker/fleans/issues/474).
Memory-provider deployments are unaffected — that path is the supported default for v1.
:::

## Provider switch

The provider name is read once at silo startup from configuration:

| Configuration key                                       | Values                           | Default  |
| ------------------------------------------------------- | -------------------------------- | -------- |
| `Fleans:Streaming:Provider`                             | `memory`, `kafka`, `azurequeue`  | `memory` |
| `Fleans:Streaming:Kafka:Brokers`                        | `host:port[,…]`                  | —        |
| `Fleans:Streaming:Kafka:ConsumerGroup`                  | string                           | `fleans` |
| `Fleans:Streaming:Kafka:TopicPrefix`                    | string                           | `fleans-` |
| `Fleans:Streaming:Kafka:QueueCount`                     | integer                          | `1`      |
| `Fleans:Streaming:Kafka:NumPartitions`                  | integer                          | `1`      |
| `Fleans:Streaming:Kafka:ReplicationFactor`              | integer                          | `1`      |
| `Fleans:Streaming:AzureQueue:ConnectionString`          | Azure Storage connection string  | —        |
| `Fleans:Streaming:AzureQueue:AccountName`               | Azure Storage account name       | —        |
| `Fleans:Streaming:AzureQueue:QueueNames`                | JSON array of strings            | `["fleans-stream-0"…"fleans-stream-7"]` |
| `Fleans:Streaming:AzureQueue:MessageVisibilityTimeout`  | TimeSpan string (e.g. `00:01:00`) | Azure SDK default (30 s) |

Match is case-insensitive. Any other value throws `ArgumentException` at startup —
add a new case to `FleanStreamingExtensions.AddFleanStreaming` if you ship another provider.

For `azurequeue`, exactly one of `ConnectionString` or `AccountName` must be set.
`ConnectionString` is used for local dev (Azurite). `AccountName` enables Managed Identity
(`DefaultAzureCredential`) for production Azure deployments.

## Choosing a provider

### `memory` — the default

Backed by `AddMemoryStreams`. Zero infrastructure. **Events are lost on silo crash.** Subscription
state survives because Aspire wires `PubSubStore` to Redis, but in-flight stream messages do not.

Right for: local development, all default tests, and single-silo deployments where you accept the
event-loss profile of a silo crash.

### `kafka` — opt-in cross-silo durability

Backed by an in-repo `Fleans.Streaming.Kafka` adapter built on `Confluent.Kafka` 2.6.1
(librdkafka 2.6.x bundled).

Right for: production rollouts that need event durability beyond a single silo, CI environments
that want broker parity with production, and topologies where a non-Orleans consumer may join
the topics later (note: the codec is currently the Orleans codec — see *limitations* below).

### `azurequeue` — opt-in cross-silo durability (Azure-native)

Backed by `Microsoft.Orleans.Streaming.AzureStorage` — the first-party Microsoft Orleans
adapter for Azure Queue Storage. This is a thin wrapper, not a custom adapter, so behaviour,
retry semantics, and dead-letter handling are governed by the Microsoft package.

Right for: Azure-native deployments that need cross-silo event durability without running Kafka.
Uses Azure Queue Storage (cheaper than Service Bus at scale; Orleans `StreamSequenceToken`
provides per-queue ordering). In dev mode, Aspire auto-provisions Azurite so no Azure
subscription is needed.

For auth options: set `ConnectionString` for Azurite or an explicit connection string; set
`AccountName` to use `DefaultAzureCredential` (Managed Identity, recommended for Container Apps).

## Production-readiness gaps

The Kafka adapter shipped in v1 is intentionally minimal. The following gaps are
tracked under [#474 — Production-ready Kafka streaming](https://github.com/nightBaker/fleans/issues/474);
pick the tier that matches your risk tolerance.

### 🔴 Won't connect

These are deploy-day blockers — your silo will fail to connect, period.

| Gap | Failure mode | Affected services |
|---|---|---|
| No `SecurityProtocol` knob | librdkafka prints `Disconnected: SASL authentication required` and the silo health check goes red | Confluent Cloud, Aiven, Redpanda Cloud, any TLS-only listener |
| No SASL/PLAIN, SCRAM, or OAUTHBEARER | librdkafka cannot authenticate; broker rejects the connection during the AdminClient `GetMetadata` warm-up | All managed brokers requiring credential auth |
| No AWS MSK IAM | librdkafka `ssl.ca.location` + token vendor not wired | Amazon MSK with IAM auth |
| No client-cert / mTLS | No `ssl.certificate.pem` / `ssl.key.pem` config | Self-managed clusters with mutual TLS |

### 🟡 Will lose data on failure

These are SLO trade-offs — connection succeeds but the durability profile is below production-typical.

| Gap | Failure mode | Mitigation today |
|---|---|---|
| `ReplicationFactor=1` default | Broker crash with unflushed log segments → events permanently lost | Set `Fleans:Streaming:Kafka:ReplicationFactor=3` once you have a 3-broker cluster |
| At-least-once delivery (offset commit after handler success) | Silo crash between handler success and offset commit → event replays | Safe by design — every consumer routes to a `WorkflowInstance` method guarded by `HasActiveEntry`. **Do not remove that guard.** See [Delivery contract](#delivery-contract--at-least-once) |
| No idempotent producer | Producer retries can reorder messages within a partition | None today; consumer-side guard above absorbs duplicates |

### 🟢 Operational gaps

These won't break a deploy, but they will cost you operationally.

| Gap | Why it hurts | Workaround |
|---|---|---|
| `NumPartitions=1` default | New topics created with 1 partition — no broker-side write fan-out per topic | Bump `Fleans:Streaming:Kafka:NumPartitions` and re-create or `kafka-topics --alter --partitions N` existing topics (forward-only; cannot shrink). See [Tuning throughput](#tuning-throughput) |
| No schema registry integration | Codec is the Orleans codec; non-Orleans consumers cannot decode the stream | Treat Kafka topics as silo-internal until a follow-up adds Avro/Protobuf framing |
| No DLQ for poison messages | A consistently-failing handler replays forever | Manual offset commit via `AdminClient` is your only escape today |
| No metrics/health-check for consumer lag | Lag is invisible to the Aspire dashboard's health UI | Run `kafka-consumer-groups.sh --describe --group fleans` against the broker |

[Manual test plan #35](https://github.com/nightBaker/fleans/tree/main/tests/manual/35-kafka-streaming) exercises the happy path against a single-broker Aspire-provisioned `fleans-kafka` resource — it validates that the at-least-once contract holds across a silo restart, but it does **not** test any production failure mode listed above.

### Production-readiness gaps — Azure Queue Storage

The Azure Queue provider is backed by the Microsoft-maintained `Microsoft.Orleans.Streaming.AzureStorage` package, which covers several production concerns automatically.

| Concern | Status |
|---|---|
| Managed Identity (`AccountName` path) | ✅ Covered — uses `DefaultAzureCredential` |
| Message retry / poison-message handling | ✅ Covered — MS provider retries up to configurable limit; exhausted messages move to `*-poison` queue automatically |
| OTLP monitoring | ✅ Covered — Orleans Streams telemetry emitted via `Orleans.Telemetry`; no extra wiring needed |
| Managed Identity token rotation | ✅ `DefaultAzureCredential` handles refresh transparently |
| Cross-queue message ordering | ⚠️ Per-queue FIFO only — ordering across the 8 default `fleans-stream-*` queues is not guaranteed (same trade-off as Kafka partitions) |

[Manual test plan #44](https://github.com/nightBaker/fleans/tree/main/tests/manual/44-azure-queue-streaming) exercises the happy path with Azurite.

## Delivery contract — at-least-once

The Kafka adapter checkpoints offsets **after** the consumer-side handler completes. On a silo
crash *between* handler success and offset commit, the next consumer instance resumes from the
last committed offset, which means the in-flight event will replay.

The replay is safe because every Fleans consumer eventually routes to a method on
`WorkflowInstance` (`CompleteActivity`, `FailActivity`, …) that opens with a stale-callback
guard:

```csharp
if (!State.HasActiveEntry(activityInstanceId))
{
    LogStaleCallbackIgnored(...);
    return;
}
```

Once the activity instance has completed once, its entry leaves `HasActiveEntry`, so a second
delivery is a logged no-op. **Do not remove that guard** without re-reading this page —
it is what makes the at-least-once contract safe for the workflow engine.

## Topic provisioning

`KafkaQueueAdapterFactory.CreateAdapter()` runs a one-shot topic-ensure step on first activation:

1. Build the expected topic list (`{TopicPrefix.TrimEnd('-')}-{0..QueueCount-1}`).
2. `AdminClient.GetMetadata` to discover what already exists on the broker.
3. `AdminClient.CreateTopicsAsync` for missing topics with the configured `NumPartitions` and
   `ReplicationFactor`.
4. `TopicAlreadyExists` and `NoError` are both treated as success (a peer silo may have just
   created the topic).

The broker-side `allow.auto.create.topics` flag is **not** required — it is documented here as
a fallback for self-managed clusters that prefer it, but managed clusters (Confluent Cloud, MSK)
disable it by default and the client-side path is what we ship.

## Aspire opt-in

The Aspire AppHost reads `FLEANS_STREAMING_PROVIDER` (default **`Redis`** since v0.3.0). **Note:** this is an Aspire-only convenience knob — silos read the runtime equivalent `Fleans__Streaming__Provider` instead (see [Configuration / Streaming](/fleans/reference/configuration/#streaming)).

```bash
# Redis-backed streams — DEFAULT, no env var needed. Reuses the orleans-redis
# container that already powers clustering + PubSubStore.
dotnet run --project Fleans.Aspire

# Memory-backed (single-silo, debug-only — drops in-flight events on restart)
FLEANS_STREAMING_PROVIDER=Memory dotnet run --project Fleans.Aspire

# Kafka-backed streams (separate Kafka cluster — provisions a fleans-kafka container)
FLEANS_STREAMING_PROVIDER=Kafka dotnet run --project Fleans.Aspire

# Azure Queue-backed streams (provisions Azurite emulator automatically)
FLEANS_STREAMING_PROVIDER=AzureQueue dotnet run --project Fleans.Aspire
```

**Redis (default):** No new container — the same `orleans-redis` instance the engine already runs for clustering and `PubSubStore` also carries stream events. Uses the third-party [`Universley.OrleansContrib.StreamsProvider.Redis`](https://github.com/MichaelSL/Universley.OrleansContrib.StreamsProvider.Redis) package (MIT, Orleans 10.x-compatible), pinned in `Fleans.ServiceDefaults.csproj`. **Production requirement:** enable Redis persistence (AOF or RDB) — without it, a Redis restart loses in-flight messages (same caveat as `PubSubStore`).

**Memory:** Provisions no container. Drops in-flight events on silo restart — single-silo debug-only.

**Kafka:** Provisions a `fleans-kafka` container and forwards `Fleans__Streaming__Provider=Kafka` + `Fleans__Streaming__Kafka__Brokers` onto the silo.

**AzureQueue:** Provisions a `fleans-azurite` container (Azurite emulator) and forwards `Fleans__Streaming__Provider=AzureQueue` + `Fleans__Streaming__AzureQueue__ConnectionString` onto the silo.

The forwarded vars are visible in the Aspire dashboard's env tab for each project.

### Connection multiplexer aliasing (Redis only)

The third-party Redis stream provider resolves a non-keyed `IConnectionMultiplexer` from DI. Aspire registers it as a *keyed* service (`AddKeyedRedisClient("orleans-redis")`). `FleanStreamingExtensions.AddRedisStreams` bridges the two with `TryAddSingleton<IConnectionMultiplexer>(...)` — reusing the same connection pool, no duplicate sockets. If another component needs a different non-keyed multiplexer, register it explicitly before `AddFleanStreaming` runs and `TryAddSingleton` will defer.

## PubSubStore vs. event durability

`PubSubStore` is the Orleans grain-storage location for **subscription state** (who is
subscribed to which stream id). Aspire wires it to Redis at `Fleans.Aspire/Program.cs`, so
subscription state already survives silo restarts under both providers.

What changes when you opt into Kafka is **event durability** — the bytes flowing between
`WorkflowEventsPublisher.Publish` and `WorkflowExecuteScriptEventHandler.OnNextAsync`. With the
in-memory provider, those bytes live in the silo's process memory. With Kafka, they land in a
broker topic before the handler runs.

## Compatibility note

`Confluent.Kafka` 2.6.1 / librdkafka 2.6.x are the supported versions. The adapter targets
`net10.0` (matching every other silo csproj in this repo).

## Tuning throughput

Throughput across the queue-backed providers is governed by two independent classes of knobs. The first is an Orleans-level concept shared by every provider; the second is Kafka-specific and does not have analogues elsewhere.

### Orleans parallelism (all queue-backed providers)

`Fleans:Streaming:Redis:TotalQueueCount`, `Fleans:Streaming:Kafka:QueueCount`, and the length of `Fleans:Streaming:AzureQueue:QueueNames` all set the same thing: the **number of Orleans pulling-agent grains** that activate across the cluster. Stream IDs hash across this many partitions; each partition is consumed by one pulling agent. More partitions = more parallel consumption.

| Provider | Knob | Default | Notes |
|---|---|---|---|
| Redis | `Fleans__Streaming__Redis__TotalQueueCount` | `8` | Configurable as of v0.2.0 (#567). |
| Kafka | `Fleans__Streaming__Kafka__QueueCount` | `8` | Was `1` before v0.2.0 (#567) — call out as a behavior change for deployments that left it unset. |
| AzureQueue | `Fleans__Streaming__AzureQueue__QueueNames__0..N` | 8 entries (`fleans-stream-0..7`) | No scalar count — tuning above OR below 8 requires populating the explicit list by hand. Mid-deployment length changes rehash Stream IDs across queues. |

**Sizing heuristic.** Start with `max(8, 2 × silo_count × cpu_per_silo)` for high-volume deployments and measure with the Orleans dashboard (wired at `Fleans.Api/Program.cs`) before raising further. In a multi-silo deployment, agents distribute across silos via the hash ring — total cluster-wide count = the configured value, per-silo count ≈ `ceil(value / silo_count)`.

**Rehash caveat (all providers).** Bumping the count rehashes Stream IDs across queues. Consistent with the project's pre-v1 stance, **expect in-flight workflow stalls across the bump window — no formal drain procedure is provided**. After the bump, new workflow instances distribute over the new queue count; in-flight instances that straddle the rehash may stall until reactivation picks them up on the new queue assignment.

**Tradeoff.** Each partition adds a pulling-agent grain per silo plus provider-side resource overhead (a Redis connection, a Kafka consumer, an AzureQueue receiver lease).

**Production caveats already documented elsewhere.** Redis streaming requires AOF or RDB persistence — see the [Streaming](#streaming) section above; Kafka requires its own cluster lifecycle ops — see [Production-readiness gaps — Kafka](#-will-lose-data-on-failure).

### Kafka-specific Kafka-side tuning

These knobs control the Kafka **broker-side** topology only. They are independent of `QueueCount` and do not multiply Orleans consumer parallelism.

| Knob | Default | What it does |
|---|---|---|
| `Fleans__Streaming__Kafka__NumPartitions` | `1` | Partition count for each Kafka topic at topic creation. Adds broker-side write parallelism (the producer can write to N partitions concurrently) and partition-level ordering granularity within one Orleans queue. **Does not** add Orleans consumer parallelism — the consumer subscribes via consumer-group `Subscribe` mode (one consumer per topic, regardless of partition count). Forward-only: `kafka-topics --alter --partitions N` can grow but not shrink, and downgrading the engine after a bump leaves topics over-partitioned. |
| `Fleans__Streaming__Kafka__ReplicationFactor` | `1` | Per-topic replication factor at topic creation. Production deployments should set to `3` once a 3-broker cluster is available — see [the durability gap above](#-will-lose-data-on-failure). |

## See also

- [Observability](/fleans/reference/observability/) — health checks, metrics, logging, tracing, dashboards, alerting
- [Deployment](/fleans/reference/deployment/) — how to wire `Fleans:Streaming:Provider` / `Fleans:Streaming:Kafka:Brokers` into Docker Compose, Kubernetes, and bare-VM deployments.
- [Self-Hosting on Kubernetes](/fleans/reference/self-hosting/) — Kafka opt-in on the Helm chart.
