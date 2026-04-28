# Fleans Load Test — Local Baseline Report

**Date:** 2026-04-28  
**Stack:** 2 × `fleans-core` (Docker) + PostgreSQL 16 + Redis 7 + nginx  
**Host:** Apple M-series (8 CPU, 7.65 GiB container memory budget)  
**k6 version:** v0.54.0  
**Branch:** `feature/load-testing-baseline-243`

---

## Results Table

| Scenario   | Max VUs | Iterations | Throughput (req/s) | p(95) duration | Error rate | Thresholds crossed | Bottleneck label |
|------------|--------:|----------:|---------------------|---------------:|:----------:|-------------------|:----------------|
| linear     |     500 |   344,815 | 1,049/s             |      174.88 ms | 96.90 %    | `http_req_failed` | **db-bound**    |
| parallel   |     500 |   322,452 |   989/s             |      175.22 ms | 98.89 %    | `http_req_failed` | **db-bound**    |
| events     |     200 |     2,166 |     6.6/s           |   32,000 ms    | 21.40 %    | `http_req_failed`, `http_req_duration`, `workflow_start_duration`, `poll_stalls` | **db-bound + event-coordination** |
| mixed      |     100 |     1,050 |     3.2/s           |   38,220 ms    | 66.66 %    | `http_req_failed`, `http_req_duration`, `workflow_start_duration` | **db-bound + event-coordination** |

---

## Peak Container CPU (docker stats)

| Container              | Linear | Parallel | Events | Mixed |
|------------------------|-------:|--------:|-------:|------:|
| fleans-core-1          |  318 % |   151 % |  342 % | 360 % |
| fleans-core-2          |  460 % |   619 % |  458 % | 456 % |
| **postgres**           | **627 %** | **606 %** | **551 %** | **483 %** |
| redis                  |    9 % |    10 % |   11 % |   5 % |

(100 % = 1 physical core saturated)

---

## Bottleneck Analysis

### Primary bottleneck: PostgreSQL write saturation

Across all scenarios PostgreSQL is the first resource to saturate. CPU peaks at **627 %** (linear, 500 VU) and stays above **480 %** even in the lower-VU mixed run. The grain persistence layer (EF Core + `MigrateAsync`) issues a separate DB roundtrip per grain state write; at 500 concurrent VUs hundreds of writes queue simultaneously.

Evidence:
- `http_req_failed` is 96–99 % for linear/parallel at 500 VUs — the API returns errors (503/500) the moment the DB is backlogged.
- `http_req_duration p(95) ≈ 175 ms` for linear/parallel despite 97 % errors — failed requests are rejected fast (DB connection pool exhausted, not slow queries).
- For events/mixed (lower VUs, longer per-iteration latency): `workflow_start_duration p(95)` reaches 38–46 s, confirming queuing inside the DB.

### Secondary bottleneck: event-coordination overhead (events/mixed)

The events scenario adds a message-delivery path on top of the DB path. `poll_stalls` reached **100 %** — every workflow that successfully started ran into a DB timeout before the Orleans grain could register the message subscription. The `message_accept_duration` and `poll_until_catch_duration` metrics were never emitted (0), confirming no workflow reached the message-wait stage.

Redis stays below 11 % CPU — it is not a bottleneck at this load level.

---

## Recommendations

| Priority | Recommendation | Expected impact |
|----------|---------------|----------------|
| P0 | **Add PgBouncer** (connection pooling) between `fleans-core` and Postgres. Default EF Core opens a connection per DbContext; at 500 VUs this exceeds `max_connections`. | Reduce connection churn, drop peak Postgres CPU 30–50 % |
| P1 | **Tune Postgres** — increase `shared_buffers`, `work_mem`, `max_connections` in the test stack (`postgresql.conf`). Default values are for < 100 concurrent clients. | Raise throughput ceiling before horizontal scaling |
| P2 | **Enable read replica routing** — `FleanQueryDbContext` is already architected for a read replica; add one to the docker-compose stack so query-side reads don't compete with writes. | Offload read traffic from the primary |
| P3 | **Grain-state write batching** — group multiple grain state flushes into a single transaction where the Orleans persistence pipeline allows. | Reduce DB roundtrips per VU |
| P4 | **Vertical scale Postgres** in the test stack — provision a container with dedicated CPU quota so measurements reflect DB capacity rather than host contention. | More accurate baseline for cloud comparison |

---

## Threshold Compliance Summary

| Metric                    | Threshold    | Linear | Parallel | Events | Mixed |
|---------------------------|:-------------|:------:|:--------:|:------:|:-----:|
| `http_req_failed`         | < 1 %        | ❌ 97 % | ❌ 99 % | ❌ 21 % | ❌ 67 % |
| `http_req_duration p(95)` | < 2,000 ms   | ✅      | ✅       | ❌ 32 s | ❌ 38 s |
| `workflow_start_duration` | < 2,000 ms   | ✅      | ✅       | ❌ 46 s | ❌ 38 s |
| `poll_stalls`             | < 1 %        | ✅      | ✅       | ❌ 100 % | ❌ 100 % |
| `poll_until_catch_duration`| < 2,500 ms  | ✅      | ✅       | ✅ (0)  | ✅ (0) |
| `message_accept_duration` | < 2,000 ms   | ✅      | ✅       | ✅ (0)  | ✅ (0) |
| `correlation_miss`        | < 1 %        | ✅      | ✅       | ✅      | n/a |

---

## Artifacts

| File | Description |
|------|-------------|
| `linear-stdout.txt` / `linear.csv` | k6 output for linear scenario |
| `parallel-stdout.txt` / `parallel.csv` | k6 output for parallel scenario |
| `events-stdout.txt` / `events.csv` | k6 output for events scenario |
| `mixed-stdout.txt` / `mixed.csv` | k6 output for mixed scenario |
| `docker-stats-linear.txt` | Container CPU/mem at 15-s intervals during linear run |
| `docker-stats-parallel.txt` | Container CPU/mem at 15-s intervals during parallel run |
| `docker-stats-events.txt` | Container CPU/mem at 15-s intervals during events run |
| `docker-stats-mixed.txt` | Container CPU/mem at 15-s intervals during mixed run |
