---
title: Performance Tuning
description: Throughput, pagination, and capacity knobs for Fleans streaming providers and persistence.
---

This page documents the operator-tunable knobs that affect Fleans throughput. For provider selection and full configuration reference, see [Streaming](/fleans/reference/streaming/) and [Persistence](/fleans/reference/persistence/).

## Stream provider sizing

### Kafka

Three knobs on `KafkaStreamingOptions`:

| Knob | Default | What it controls |
|------|---------|------------------|
| `Fleans:Streaming:Kafka:QueueCount` | `8` | Orleans-side pulling-agent count — one consumer per queue, each subscribed to one topic. Matches `Redis:TotalQueueCount` semantics. |
| `Fleans:Streaming:Kafka:NumPartitions` | `1` | Kafka-side per-topic partition count at topic creation. Independent of `QueueCount` — does not multiply Orleans consumer parallelism. Forward-only via `kafka-topics --alter --partitions N`. |
| `Fleans:Streaming:Kafka:ReplicationFactor` | `1` | Per-topic broker replication. Set to `3` in multi-broker clusters for durability. |

Production deployments: see [#474](https://github.com/nightBaker/fleans/issues/474) for security (SASL/TLS), durability (idempotent producer), and operational (DLQ, lag metrics) gaps not yet shipped.

### Redis

One knob:

| Knob | Default | What it controls |
|------|---------|------------------|
| `Fleans:Streaming:Redis:TotalQueueCount` | `8` | Orleans-side queue count for the Redis stream provider. Each silo's pulling agents spread across this many queues. FIFO is guaranteed per stream; cross-instance traffic distributes across `TotalQueueCount` Redis queues. |

Redis streaming is supported via a third-party stream-provider package (not in-repo). Helm chart support for Redis streaming knobs is tracked in [#610](https://github.com/nightBaker/fleans/issues/610).

### AzureQueue

`QueueNames` list length (default `8` entries). Tune by populating `Fleans__Streaming__AzureQueue__QueueNames__0..N` environment variables.

## Persistence query pagination

### User-task list query

The user-task list query is paginated via [Sieve](https://github.com/Biarity/Sieve) and applies the assignee/candidate-group filter at the SQL level on PostgreSQL (via `FromSqlInterpolated` + JSON-text LIKE). The PostgreSQL path (`PostgresUserTaskFilterStrategy`) avoids materializing the full table. SQLite users with similar-scale workloads should switch to PostgreSQL via `FLEANS_PERSISTENCE_PROVIDER=Postgres`.

### Event store reads

`EfCoreEventStore.ReadEventsAsync` honors a configurable row limit. The default is `MaxEventsPerLoad = 1000` (from `FleansPersistenceOptions`).

| Knob | Default | What it controls |
|------|---------|------------------|
| `Persistence:MaxEventsPerLoad` | `1000` | Maximum number of events loaded per grain-activation replay. If a workflow exceeds this limit, the silo logs: *"Increase Persistence:MaxEventsPerLoad if this is intentional."* |

For typical workflow volumes, the default is appropriate. Only tune this if you see the warning message in silo logs for workflows with unusually long event histories.

## Related

- [Streaming](/fleans/reference/streaming/) — provider selection and config matrix.
- [Persistence](/fleans/reference/persistence/) — SQLite vs Postgres trade-offs.
- [`docs/conventions/streaming.md`](https://github.com/nightBaker/fleans/blob/main/docs/conventions/streaming.md) — contributor-side tuning guide.
