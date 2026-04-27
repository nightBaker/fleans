---
title: Streaming providers
description: How Fleans publishes domain events on Orleans Streams and how to switch between the in-memory and Kafka-backed providers.
---

Fleans publishes domain events (`EvaluateConditionEvent`, `EvaluateActivationConditionEvent`,
`ExecuteScriptEvent`, …) through **Orleans Streams**. The stream provider is pluggable at startup:
the default is Orleans' in-memory provider, and a Kafka-backed provider ships alongside it for
cross-silo event durability.

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

#### What v1 does NOT give you

- **Production-managed Kafka with SASL/TLS authentication.** v1 ships **plaintext brokers
  only**. `Confluent Cloud`, `MSK with IAM`, `Aiven`, and `Redpanda Cloud` all require a
  follow-up that exposes `SecurityProtocol`, SASL keys, and TLS material — until that ships,
  do not point this at a managed broker.
- **Exactly-once delivery.** Orleans Streams over `IQueueAdapterFactory` + Kafka offsets is
  **at-least-once** — see *Delivery contract* below.
- **Multi-broker topic replication.** `ReplicationFactor` defaults to `1`. Lose the broker, lose
  unflushed events. Tunables for partition count and replication factor are a follow-up.

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

The Aspire AppHost reads `FLEANS_STREAMING_PROVIDER` (default `Memory`). Setting it to `Kafka`
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
