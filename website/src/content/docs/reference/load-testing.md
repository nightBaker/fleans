---
title: Load testing
description: Throughput, latency, and bottlenecks observed when Fleans runs under load — both on a developer laptop (Docker Compose) and on Azure Container Apps via Azure Load Testing.
---

Fleans ships a load-test suite under `tests/load/`. Two drivers are wired up: **k6** (the
primary suite) and **Locust** (port of the same scenarios for Azure Load Testing). This page
summarises the most recent baseline measurements and the bottleneck profile they expose.

:::note
This page tracks load-test results for the **current public release** of Fleans only. Older
runs are removed when a new version ships — for historical numbers, check the report files
under `tests/load/results/` in the matching git tag.

**Currently published:** Fleans **v0.1.0**.
:::

## Scenarios

Four scenario scripts live in parallel under `tests/load/scripts/` (k6) and `tests/load/locust/`
(Locust). They share fixtures under `tests/load/fixtures/`.

| Scenario | Purpose | Fixture / process id |
|----------|---------|----------------------|
| `linear`   | Pure throughput — `Start → ScriptTask → End`. Measures `/Workflow/start` HTTP latency only.        | `load-linear`   |
| `parallel` | 3-branch fork/join, each branch a `ScriptTask`. Same HTTP-latency surface, more state writes per start. | `load-parallel` |
| `events`   | Three-phase event-driven loop: start → poll for `waitMessage` → POST `/Workflow/message`.          | `load-events`   |
| `mixed`    | 40 / 30 / 30 weighted blend of the above three.                                                    | (composite)     |

The suite is documented in detail in `tests/load/README.md`.

## Recent baselines

Two reports exist in the repository for the current release. Each is anchored to its driving
hardware/SKU and the date it was run:

- **Local Docker Compose** (`tests/load/results/local/report.md`) — 2 silos in `docker compose`,
  500 concurrent virtual users per scenario. Hardware: developer laptop. Originally produced for
  issue #243.
- **Azure Container Apps + Azure Load Testing** (`tests/load/results/azure-2026-04-29/report.md`) —
  2 silos behind the Container Apps built-in load balancer, Postgres Burstable B2s, Redis Basic C0.
  500 VUs per scenario plus a 2 000-VU scale test against the same topology.

Both reports include per-scenario throughput / latency tables, peak container CPU, threshold compliance, and ranked recommendations. The summary below pulls the headline numbers; reach for the
report files for raw CSVs and full bottleneck attribution.

### Fleans v0.1.0 — 2026-04-28 — Local Docker Compose @ 500 VU

| Scenario   | Max VUs | Iterations | Throughput (req/s) | p(95) duration | Error rate | First bottleneck |
|------------|--------:|----------:|--------------------:|---------------:|:----------:|-----------------:|
| `linear`   |     500 |   344 815 | **1 049 / s** | 174.88 ms | 96.90 % | Postgres write saturation |
| `parallel` |     500 |   322 452 |   989 / s     | 175.22 ms | 98.89 % | Postgres write saturation |
| `events`   |     200 |     2 166 |     6.6 / s   | 32 000 ms | 21.40 % | Postgres + event coordination |
| `mixed`    |     100 |     1 050 |     3.2 / s   | 38 220 ms | 66.66 % | Postgres + event coordination |

The local laptop run is **db-bound**: PostgreSQL CPU peaks above 600 % across all scenarios. The error spikes are connection-pool exhaustion (`max_connections=100` default vs. 200+ EF Core
connections under burst), not slow queries.

### Fleans v0.1.0 — 2026-04-29 — Azure Container Apps @ 500 VU (and a 2 000-VU scale test)

| Scenario | Reqs | RPS | HTTP fail % | p50 | p95 | max |
|----------|-----:|----:|------------:|----:|----:|----:|
| `linear` @ 500 VU                | 33 907 | 113 | 0.0 % | 1.9 s | **8.4 s** | 11.4 s |
| `parallel` @ 500 VU              | 22 701 | 76  | 0.0 % | 1.9 s | **13.2 s** | 15.5 s |
| `events` @ 500 VU (`workflow_start`) | 13 915 | 47  | 0.0 % | 9.2 s | 18.5 s | 24.4 s |
| `events` @ 500 VU (`poll_stall`) | 13 760 | 46  | **100 %** | — | — | — |
| `mixed` @ 500 VU (parallel branch) | 3 653 | 12  | **6.8 %** | 4.1 s | 30.7 s | 33.7 s |
| `linear` @ **2 000** VU          | 32 492 | **108.5** | **47.0 %** | **13.6 s** | 33.5 s | 39.8 s |

The Azure Container Apps target is **silo-CPU bound**. With 2 × 0.5 vCPU silos the API tops out
around **110 RPS** regardless of VU count — adding load adds queue, not work. The 2 000-VU run
is the same throughput as the 500-VU run, just with 47 % HTTP 500s instead of queued requests.

Two bottleneck profiles compared at the same VU count:

| Run | Throughput | p95 (`workflow_start`) | HTTP fail % |
|---|---:|---:|---:|
| Docker laptop, 2 silos, 8-core host | 1 049 / s | 175 ms | **97 %** |
| Container Apps, 2 silos × 0.5 vCPU | 113 / s   | 8 405 ms | 0 % |

Same code, same fixtures, same scripts. The bottleneck moves from Postgres to silo CPU when the
silo SKU shrinks far enough; both regimes are valuable for shaping a sizing decision.

## What the events scenario revealed

The events scenario reported a **100 % `poll_stall` rate** in both the 2-silo and the 1-silo
re-run on Azure. We chased it because that's a striking result. After investigation:

- **The fixture and the Locust port are correct.** A single-instance probe with no other load
  showed `start → step1 → waitMessage` in <500 ms; the message correlated and the workflow
  completed.
- **Under 500 VU on 0.5 vCPU silos**, the silo cannot dispatch the `step1 → waitMessage`
  transition within the test's 3 s poll budget. The test catches this correctly.

Three **incidental engine bugs** surfaced and are worth knowing about when you scale Fleans:

### 1. Stale Orleans membership after Container Apps scale-down

When Container Apps replicas decrease, the Redis clustering table keeps the dead silo's
endpoint until Orleans probes converge it (~1 minute). During that window, grain calls route
to the dead silo and stateless-worker activations stay registered there:

```
warn: Orleans.Runtime.Messaging.NetworkingTrace
Connection attempt to endpoint S100.100.200.74:11111 failed
warn: Orleans.Runtime.MembershipService.SiloHealthMonitor
Did not get response for probe #18 to silo ...
```

If you actively scale Container Apps replicas in Fleans clusters today, expect a window where
new starts complete but their script tasks hang. The mitigation is graceful-shutdown logic
that explicitly tombstones the silo's row in Orleans's membership table; the fast workaround
is to force a new revision (any env-var change triggers it) which causes a clean cluster
re-election.

### 2. Default `Fleans:Streaming:Provider=memory` is unsafe for more than 1 silo

In-memory Orleans streams are per-silo only. When the publisher and the consumer activation
live on different silos, events are dropped:

```
warn: Orleans.Streams.StreamConsumerExtension
[GrainId worfklowevaluateconditioneventhandler ...]
got an item for subscription ..., but I don't have any subscriber
for that stream. Dropping on the floor.
```

Any non-memory provider (see [Streaming providers](./streaming.md)) is multi-silo-safe; the default
**memory** provider is **dev-only** despite being the default. Set
`Fleans__Streaming__Provider=Kafka` or `Fleans__Streaming__Provider=AzureQueue` for any
deployment with more than one silo.

### 3. Brittle correlation expression parser

`WorkflowExecution.ProcessRegisterMessage` extracts the correlation variable name with a
literal `"= "` (= + space) prefix strip, not a real expression evaluator:

```csharp
var variableName = messageDef.CorrelationKeyExpression.StartsWith("= ")
    ? messageDef.CorrelationKeyExpression[2..]
    : messageDef.CorrelationKeyExpression;
```

Any expression that isn't the exact form `"= variableName"` (`"=requestId"`,
`"= request.Id"`, `"= upper(requestId)"`) falls through and is looked up as a literal
variable name, then throws `InvalidOperationException` at runtime when not found.
Real Zeebe-style FEEL expressions are silently broken. The fixture used in our load tests
happens to use the supported form, so we never hit it — but other BPMN you import almost
certainly won't.

## Sizing recommendations

In priority order, based on what we measured:

| # | Action | Why |
|---|--------|-----|
| P0 | **Bigger silo CPU.** 0.5 vCPU is too small. Move to 1 vCPU / 2 GiB minimum, ideally 2 vCPU. | Silo CPU is the first thing that pegs on Azure. |
| P1 | **Add PgBouncer** between silos and Postgres. EF Core opens a connection per `DbContext`; under burst this exhausts `max_connections`. | The local Docker run failed at this exact wall (97 % errors). |
| P2 | **Bigger Postgres SKU.** Burstable B2s is fine for the events case at low VU; for sustained 500 VU+ pick General Purpose D2ds_v5 or larger. | Removes the secondary cliff that follows P0. |
| P3 | **Switch to a non-memory stream provider.** `Fleans__Streaming__Provider=Kafka` or `Fleans__Streaming__Provider=AzureQueue` once you're past 1 silo. | The memory default silently drops cross-silo events. |
| P4 | **Pre-warm before measurement.** The first 30 s of each run mixes engine ramp-up with cold-start grain activation. | Cleaner reported tails. |

## Reproducing the runs

Both report files include a "How to reproduce" section that lists the exact `az`/`docker`
commands. The Locust scripts under `tests/load/locust/` are uploaded directly to Azure Load
Testing as the test plan; the k6 scripts under `tests/load/scripts/` are run locally against
either the Aspire stack or `tests/load/generated/docker-compose.yml`.

For the Azure run specifically, you'll need:

- Azure subscription with access to `Microsoft.ContainerRegistry`, `Microsoft.DBforPostgreSQL`,
  `Microsoft.Cache`, `Microsoft.App`, and `Microsoft.LoadTestService` resource providers
  (auto-register on first use).
- Either local `az` CLI (Python 3.13 may have a `pyexpat` issue depending on platform; if so,
  use `mcr.microsoft.com/azure-cli` from Docker), or just the Azure portal.
- The released `fleans-api` container image, re-tagged into your ACR. `docker pull` the image
  from `ghcr.io/nightbaker/fleans-api:<version>`, then `docker tag` + `docker push` it to your
  ACR (e.g. `myacr.azurecr.io/fleans-api:<version>`). Optionally verify the upstream image via
  `cosign verify` first (see [Self-host with Docker Compose](/fleans/guides/self-host-docker-compose/)
  for the canonical command).

  ```bash
  VERSION=v0.1.0-beta
  ACR=myacr.azurecr.io
  docker pull ghcr.io/nightbaker/fleans-api:$VERSION
  docker tag ghcr.io/nightbaker/fleans-api:$VERSION $ACR/fleans-api:$VERSION
  docker push $ACR/fleans-api:$VERSION
  ```

  **Add `fleans-web` only if you also want the management UI in Azure** — it isn't strictly
  needed for the tests themselves.

Estimated cost: ~$3 / day idle for the resource group, ~$5 in test runs.
