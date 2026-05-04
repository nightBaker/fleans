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

| Configuration key                              | Values            | Default  |
| ---------------------------------------------- | ----------------- | -------- |
| `Fleans:Streaming:Provider`                    | `memory`, `kafka` | `memory` |
| `Fleans:Streaming:Kafka:Brokers`               | `host:port[,…]`   | —        |
| `Fleans:Streaming:Kafka:ConsumerGroup`         | string            | `fleans` |
| `Fleans:Streaming:Kafka:TopicPrefix`           | string            | `fleans-` |
| `Fleans:Streaming:Kafka:QueueCount`            | integer           | `1`      |
| `Fleans:Streaming:Kafka:NumPartitions`         | integer           | `1`      |
| `Fleans:Streaming:Kafka:ReplicationFactor`     | integer           | `1`      |

Match is case-insensitive. Any other value throws `ArgumentException` at startup —
add a new case to `FleanStreamingExtensions.AddFleanStreaming` if you ship another provider.

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
| `QueueCount=1`, `NumPartitions=1` defaults | All consumers share one partition → no horizontal fan-out | Tune both up before scaling out the silo |
| No schema registry integration | Codec is the Orleans codec; non-Orleans consumers cannot decode the stream | Treat Kafka topics as silo-internal until a follow-up adds Avro/Protobuf framing |
| No DLQ for poison messages | A consistently-failing handler replays forever | Manual offset commit via `AdminClient` is your only escape today |
| No metrics/health-check for consumer lag | Lag is invisible to the Aspire dashboard's health UI | Run `kafka-consumer-groups.sh --describe --group fleans` against the broker |

[Manual test plan #35](https://github.com/nightBaker/fleans/tree/main/tests/manual/35-kafka-streaming) exercises the happy path against a single-broker Aspire-provisioned `fleans-kafka` resource — it validates that the at-least-once contract holds across a silo restart, but it does **not** test any production failure mode listed above.

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

The Aspire AppHost reads `FLEANS_STREAMING_PROVIDER` (default `Memory`). **Note:** this is an Aspire-only convenience knob — silos read the runtime equivalent `Fleans__Streaming__Provider` instead (see [Configuration / Streaming](/fleans/reference/configuration/#streaming)). Setting it to `Kafka`
provisions a Kafka container and forwards two environment variables onto the silo:

```bash
FLEANS_STREAMING_PROVIDER=Kafka dotnet run --project Fleans.Aspire
```

The forwarded vars (`Fleans__Streaming__Provider=Kafka`,
`Fleans__Streaming__Kafka__Brokers=<endpoint>`) are visible in the Aspire dashboard's env tab.
Default-mode Aspire (`dotnet run --project Fleans.Aspire` with no env var) provisions no Kafka
container.

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

## See also

- [Observability](/fleans/reference/observability/) — health checks, metrics, logging, tracing, dashboards, alerting
- [Deployment](/fleans/reference/deployment/) — how to wire `Fleans:Streaming:Provider` / `Fleans:Streaming:Kafka:Brokers` into Docker Compose, Kubernetes, and bare-VM deployments.
- [Self-Hosting on Kubernetes](/fleans/reference/self-hosting/) — Kafka opt-in on the Helm chart.
