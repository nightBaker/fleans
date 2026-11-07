---
title: Performance Tuning
description: Throughput, pagination, and capacity knobs for Fleans streaming providers and persistence.
---

This page documents the operator-tunable knobs that affect Fleans throughput. For provider selection and full configuration reference, see [Streaming](/fleans/reference/streaming/) and [Persistence](/fleans/reference/persistence/).

<figure class="arch-diagram" style="--arch-ratio: 980 / 538">
  <iframe data-arch-src="/fleans/diagrams/perf-tuning-map.html" src="/fleans/diagrams/perf-tuning-map.html?embed=1" title="Performance tuning map" loading="lazy"></iframe>
  <figcaption>Throughput knobs and their defaults: stream queue counts set how many pulling agents each silo runs; persistence knobs bound event-replay size and user-task query cost. <a href="/fleans/diagrams/perf-tuning-map.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

## Stream provider sizing

### Kafka

Three knobs on `KafkaStreamingOptions`:

| Knob | Default | What it controls |
|------|---------|------------------|
| `Fleans:Streaming:Kafka:QueueCount` | `8` | Orleans-side pulling-agent count — one consumer per queue, each subscribed to one topic. Matches `Redis:TotalQueueCount` semantics. |
| `Fleans:Streaming:Kafka:ReplicationFactor` | `3` | Per-topic broker replication, capped at the live broker count (`1` on a single broker). |

Production deployments: SASL / TLS brokers are configured as in [Kafka security](/fleans/reference/streaming/#kafka-security-sasl--tls); see [Production-readiness gaps](/fleans/reference/streaming/#production-readiness-gaps) for what the adapter still leaves to you. Poison records can be routed to a dead-letter topic — see [Streaming](/fleans/reference/streaming/#poison-records-and-the-dead-letter-topic).

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
| `Persistence:MaxEventsPerLoad` | `1000` | Maximum number of events loaded per grain-activation replay. A load past this limit throws, so the instance cannot activate until the limit is raised. The error says: *"Increase Persistence:MaxEventsPerLoad if this is intentional."* |
| `Persistence:SnapshotInterval` | `100` | A workflow instance snapshot (its full state) is written every this many events. Activation loads the snapshot and replays at most about this many events after it. Lower it for faster reactivation, raise it to write less. |

For typical workflow volumes, the defaults are appropriate. `MaxEventsPerLoad` must be greater than `SnapshotInterval`; the silo refuses to start otherwise. A failed snapshot write (log EventId 1131) is retried for about 8.5 minutes (1145 per failed retry, 1143 on success, 1144 when the retries give up); after that the instance's next activation writes it. If snapshot writes keep failing, the event history after the last snapshot keeps growing. Fix the cause before it passes `MaxEventsPerLoad`, or raise the limit.

### Long-running instances

An instance keeps its full activity history in memory and in every snapshot, and nothing is pruned while it exists. A step adds to that history, so a very long loop gets more expensive as it runs:

| Script + gateway steps | Snapshot size | Total written by snapshots |
|---|---|---|
| 100 | 41 KB | about 0.2 MB |
| 2,000 | 0.8 MB | about 74 MB |
| 10,000 | about 4 MB | about 1.9 GB |

The totals are calculated from the measured sizes at the default `SnapshotInterval`. The figures use almost no variables, so large variables make them bigger. Each instance keeps a single snapshot, so the totals are write load rather than disk use.

Most workflows never get close to this. If yours loops thousands of times, you can:

- Move the repeated work into a [Call Activity](/fleans/concepts/bpmn-support/), because each child instance has its own state.
- Raise `Persistence:SnapshotInterval` to write less often. It must stay below `Persistence:MaxEventsPerLoad`.

## Related

- [Streaming](/fleans/reference/streaming/) — provider selection and config matrix.
- [Persistence](/fleans/reference/persistence/) — SQLite vs Postgres trade-offs.
- [`docs/conventions/streaming.md`](https://github.com/nightBaker/fleans/blob/main/docs/conventions/streaming.md) — contributor-side tuning guide.
