# Fleans Load Test — Azure Load Testing (Locust) Baseline

**Date:** 2026-04-29
**Driver:** Azure Load Testing service (Locust test type, `load` extension v2.1.0)
**Test plans:** `tests/load/locust/{linear,parallel,events,mixed}.py` (Locust port of the k6 scripts in `tests/load/scripts/`)
**Target:** Fleans deployed to Azure Container Apps in `eastus2`

---

## Stack

| Component | Resource | SKU / config |
|---|---|---|
| API | Container App `ca-fleans-core` | 2 replicas, 0.5 vCPU + 1 GiB each; ingress public via built-in HTTPS LB |
| Database | Postgres Flexible Server `pg-fleans-loadtest` | Burstable B2s (2 vCPU / 4 GiB), `eastus2` |
| Clustering / DataProtection | Azure Cache for Redis `redis-fleans-loadtest` | Basic C0 (250 MB), `eastus` |
| Image registry | ACR `acrfleans45d967` | Standard, eastus2 |
| Container image | `acrfleans45d967.azurecr.io/fleans-core:latest` | built from `Fleans.Api/Dockerfile` (.NET 10, Release) |
| Wiring | env on the silo | `FLEANS_LOAD_TEST_MODE=true`, `Persistence__Provider=Postgres`, Redis 6380/SSL |
| Test driver | Azure Load Testing `alt-fleans-loadtest` | 2 engines / scenario (4 for the 2000-VU run), `eastus2` |

Public endpoint: `https://ca-fleans-core.delightfulflower-772d29c6.eastus2.azurecontainerapps.io`

---

## Run matrix

All scenarios — 5 minutes sustained, ramp-rate 50 VU/s (200 VU/s for the 2 000-VU run). `LOCUST_USERS` is the **total** concurrent VUs across all engines.

| # | Scenario | VUs | Engines | Test ID | Run ID |
|---|---|---:|---:|---|---|
| 1 | linear | 500 | 2 | `linear-locust` | `linear-1` |
| 2 | parallel | 500 | 2 | `parallel-locust` | `parallel-1` |
| 3 | events | 500 | 2 | `events-locust` | `events-1` |
| 4 | mixed (40/30/30) | 500 | 2 | `mixed-locust` | `mixed-1` |
| 5 | linear (scale test) | 2 000 | 4 | `linear-2k-locust` | `linear-2k-1` |
| 6 | events (re-run, **single silo, fresh Orleans membership**) | 500 | 2 | `events-locust` | `events-2` |

---

## Headline results

| Run | Reqs | RPS | HTTP fail % | p50 | p90 | p95 | p99 | max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| linear @ 500 VU | 33 907 | **113** | 0.0 % | 1.9 s | 8.0 s | **8.4 s** | 10.1 s | 11.4 s |
| parallel @ 500 VU | 22 701 | 76 | 0.0 % | 1.9 s | 12.6 s | **13.2 s** | 14.4 s | 15.5 s |
| events @ 500 VU (`workflow_start`) | 13 915 | 47 | 0.0 % | 9.2 s | 13.1 s | **18.5 s** | 22.5 s | 24.4 s |
| events @ 500 VU (`poll_state`) | 90 286 | 302 | 0.0 % | 0.17 s | 0.28 s | 0.40 s | 2.0 s | 4.5 s |
| events @ 500 VU (`poll_stall`)¹ | 13 760 | 46 | **100 %** | — | — | — | — | — |
| mixed @ 500 VU (linear branch) | 11 243 | 38 | 0.0 % | 2.7 s | 9.8 s | 15.4 s | 21.1 s | 24.0 s |
| mixed @ 500 VU (parallel branch) | 3 653 | 12 | **6.8 %** | 4.1 s | 26.9 s | 30.7 s | 31.3 s | 33.7 s |
| mixed @ 500 VU (events `workflow_start`) | 4 891 | 16 | 0.0 % | 6.8 s | 12.3 s | 17.2 s | 21.5 s | 24.2 s |
| **linear @ 2 000 VU** | 32 492 | **108.5** | **47.0 %** (15 278 × HTTP 500) | **13.6 s** | 31.9 s | **33.5 s** | 37.3 s | 39.8 s |
| events-2 (1 silo) `workflow_start` | 10 297 | 36 | 0.0 % | 7.7 s | 20.3 s | **21.1 s** | 22.1 s | 23.8 s |
| events-2 (1 silo) `poll_state` | 25 532 | 89 | 0.0 % | 1.2 s | 2.1 s | 3.2 s | 3.9 s | 4.6 s |
| events-2 (1 silo) `poll_stall`¹ | 10 143 | 35 | **100 %** | — | — | — | — | — |

¹ `poll_stall` is the synthetic Locust event the events test fires whenever the per-iteration poll loop exhausts its 3 s wall-clock budget without seeing `waitMessage` in `activeActivityIds`. 100 % stall rate means *zero* iterations made it past Phase 2 in 5 min of load — same as the original 2-silo `events-1` run.

---

## Bottleneck analysis

### Throughput plateau ≈ 110 RPS

The single most important number: **at 2 × 0.5 vCPU silos against Burstable B2s Postgres, the API tops out at ~108–113 RPS regardless of VU count**. 500 VU and 2 000 VU produced identical aggregate throughput; the only thing that changed was where the queue lived (in flight HTTP requests vs. visible 500s).

Little's Law on the 500-VU linear run: VUs ≈ RPS × avg latency = 113 × 4.2 s ≈ 475. The 25 missing VUs are think-time (`constant(0.1)` between iterations). So the cluster is *fully* saturated already at 500 VU; everything beyond is queueing.

### Why the events scenario is hopeless at 500 VU

Phase 2 of the events test polls `GET /Workflow/instances/{id}/state` looking for `waitMessage` in `activeActivityIds`. That field is populated by the EF read-side projection. Under saturation:

- `workflow_start` p95 = **18.5 s** — the start grain itself is queued behind Postgres writes.
- `poll_state` p95 = 397 ms — the read endpoint is fast, but the *projection lag* it serves is huge.
- 100 % of polls timed out at the 3 s wall-clock budget without the catch event ever showing up.

Reading the projection is fine; the projection just hasn't seen the message-subscription event yet because the writer is queued. Same root cause as on the local run; just exposed at lower VU because the Azure target is much smaller.

### Why mixed is the first scenario with HTTP errors

Linear and parallel each survive 500 VU with 0 % HTTP failures, just elevated tails. Combine them in the mixed run (40 % linear + 30 % parallel + 30 % events at the same total VU budget) and the parallel branch starts returning 5xx (6.8 %). Reason: parallel workflows write more rows per start (one `WorkflowInstance` + one entry per fork branch + the gateway state record); when DB I/O is already at capacity from the linear branch, parallel writes start timing out at the API layer.

### Events scenario: not a test bug, a saturation reading

The events scenario reported **100 % poll-stalls** under both 2-silo (`events-1`) and 1-silo, fresh-Orleans-membership (`events-2`) configurations. To rule out a fixture / test-port bug we ran the full reproduction:

1. **Single-instance probe (no load).** A fresh `load-events` start advanced `step1 → waitMessage` in **<500 ms**, the test's message correlated, and the workflow completed normally. Fixture and Locust port are correct.
2. **Re-run with 1 silo + cleared Orleans membership in Redis** (`events-2`). Same 100 % stall rate, `workflow_start` p95 = 21 s. Confirms the stall is *not* caused by cross-silo grain dispatch.
3. **Conclusion.** Under 500 VU on 0.5 vCPU × 1 silo, the silo CPU is saturated such that the `start → step1 → waitMessage` transition (which runs *after* `/Workflow/start` returns) takes much longer than the test's 3 s poll budget. Scaling silo CPU per recommendation P0 would unblock this.

Two **incidental real bugs** surfaced during this investigation that are independent of saturation and worth filing separately:

#### Bug A — Stale Orleans membership after Container Apps scale-down

When Container Apps replicas decrease, the Redis `clustering` table keeps the dead silo's address until Orleans probes time it out. We observed:

```
Connection attempt to endpoint S100.100.200.74:11111:136466568 failed
... Did not get response for probe #18 to silo S100.100.200.74:11111:136466568
```

For ~1 minute after a scale-down, grain calls were routed to the dead silo and ScriptExecutor activations remained registered there. Workflows hung. The fix is graceful-shutdown handling that explicitly removes the silo entry from Orleans's membership table, or an `IMembershipTable` bias toward more aggressive tombstoning under Container Apps churn.

#### Bug B — Default `Fleans:Streaming:Provider=memory` is unsafe for >1 silo

In the original `events-1` run with 2 silos we observed:

```
warn: Orleans.Streams.StreamConsumerExtension[103407]
[GrainId worfklowevaluateconditioneventhandler ...]
got an item for subscription ..., but I don't have any subscriber
for that stream. Dropping on the floor.
```

In-memory Orleans streams are per-silo only. When the publisher and the consumer activation live on different silos, events are dropped. The `Fleans.Streaming.Kafka` provider exists and is multi-silo-safe, but it's opt-in and the default (`memory`) silently breaks multi-silo deployments. Either `memory` should refuse to start with >1 silo, or the README/`CLAUDE.md` should flag the constraint loudly.

#### Bug C — Brittle correlation expression parser

`WorkflowExecution.ProcessRegisterMessage` strips the `"= "` prefix from `messageDef.CorrelationKeyExpression` to extract the variable name:

```csharp
var variableName = messageDef.CorrelationKeyExpression.StartsWith("= ")
    ? messageDef.CorrelationKeyExpression[2..]
    : messageDef.CorrelationKeyExpression;
```

Anything other than the exact form `"= variableName"` (`"=requestId"`, `"= request.Id"`, `"= upper(requestId)"`) falls through and is looked up as a literal variable name, which won't exist → `InvalidOperationException` thrown synchronously inside the workflow. Real Zeebe-style expressions are silently broken. The fixture happens to use the supported form, but the parser should either evaluate proper expressions or reject malformed ones at deploy time, not at runtime.

### What 2 000 VU shows

- **47 % HTTP 500s** — the Container App is no longer politely queueing; it's returning errors.
- p50 13.6 s — a typical user waits ~14 s for a single workflow start.
- Throughput unchanged from 500 VU (108 vs 113 RPS) — adding load adds queue, not work.

This run essentially demonstrates that 2 × 0.5 vCPU + B2s Postgres has zero headroom past 500 VU on this workload.

### Comparing to PR #419's local Docker run (same fixtures, same code, same scripts)

| Run | Throughput | p95 (workflow_start) | HTTP fail % |
|---|---:|---:|---:|
| #419 linear @ 500 VU (Docker on Mac, 2 silos) | **1 049 / s** | 175 ms | **97 %** |
| this linear @ 500 VU (Container Apps, 2 silos × 0.5 vCPU) | 113 / s | 8 405 ms | 0 % |

Two very different bottlenecks at the same VU count:
- Local Docker: connection-pool exhaustion. Postgres rejects requests fast → 175 ms p95 but 97 % failures.
- Azure: silo CPU starvation. The 0.5 vCPU container can't dequeue requests fast enough → 8 s p95 but 0 % failures (until the 2 000 VU run).

The Container App SKU is the dominant factor. A laptop-class Docker silo (8 cores) easily sources 1 k RPS; a 0.5 vCPU container saturates an order of magnitude lower. Cloud numbers in this report should be read as a **qualitative shape check**, not a comparison of absolute capacity.

---

## Recommendations

| # | Action | Expected effect |
|---|---|---|
| P0 | **Bigger silo CPU.** 0.5 vCPU is too small for any real load. Try 1 vCPU / 2 GiB and re-run linear at 500 VU; expect ~3× throughput. | Move ceiling from ~110 to ~300+ RPS per replica |
| P1 | **Scale silos horizontally.** With min/max replicas 4–10 + a CPU-based autoscale rule on `cpu>70`, RPS will climb proportional to replica count *until* Postgres becomes the bottleneck (matches #419's finding). | Better tail latency, higher burst capacity |
| P2 | **Bigger Postgres.** Burstable B2s (2 vCPU) at this load is a slow ceiling. Move to General Purpose D2ds_v5 (2 vCPU / 8 GiB, dedicated) or D4ds_v5 (4 vCPU / 16 GiB). | Removes the cause of `events` poll-stalls |
| P3 | **Add PgBouncer** between the silo and Postgres. EF Core opens a connection per DbContext; under burst, you'll exceed `max_connections` even on a bigger SKU (we hit this verbatim on the local run earlier today). | Removes the connection-pool cliff |
| P4 | **Pre-warm before measurement.** The first 30 s of each run mixes engine ramp-up with cold-start grain activation, which inflates p99/max. A 30-s warm-up phase in the test plan would clean up tail-latency numbers. | Cleaner reported tails |
| P5 | **Consider the streams provider for events.** The events scenario's poll-stall is partly the projection-lag issue; switching to Kafka-backed streams (already supported in Fleans) might shorten the visibility window. | Lower events-scenario poll budget needed |

---

## Cost

| Resource | Approximate cost |
|---|---|
| 5 runs × Azure Load Testing | ~328 VU-hours total (41 + 41 + 41 + 41 + 164) ≈ **$5** |
| Idle infra (1 day) | ACR Standard $0.67 + Postgres B2s $1.00 + Redis Basic C0 $0.50 + Container Apps idle ~$0.50 = **~$3 / day** |
| Active container compute during runs | marginal (~$0.10 total) |

Total spend for this report: **≈ $5 in test runs + $3/day idle** if you leave the stack up.

To stop billing entirely:
```bash
docker run --rm -v ~/.azure:/root/.azure azd:latest \
  az group delete -n fleans-loadtest -y --no-wait
```

To pause cheaply (Postgres + Container App stop; Redis Basic doesn't support stop):
```bash
docker run --rm -v ~/.azure:/root/.azure azd:latest \
  az containerapp update -g fleans-loadtest -n ca-fleans-core --min-replicas 0 --max-replicas 0
docker run --rm -v ~/.azure:/root/.azure azd:latest \
  az postgres flexible-server stop -g fleans-loadtest -n pg-fleans-loadtest
```

---

## Artifacts

| Path | Description |
|---|---|
| `engine{1,2}_results.csv` (in `linear-1`/.../`mixed-1`) | Per-engine raw JMeter-style CSVs (timeStamp,elapsed,label,responseCode,success,…) |
| `linear-2k-1/engine{1..4}_results.csv` | Same schema, 4 engines |
| `csv.zip`, `logs.zip` per run dir | Zipped originals from `az load test-run download-files` |
| `tests/load/locust/{linear,parallel,events,mixed}.py` | Locust source |
| Portal dashboards | `https://portal.azure.com/#@.../testRunReport.ReactView/...testRunId/<run-id>` (URLs in `az load test-run show` output) |

---

## How to reproduce

Prereqs: Azure subscription, Docker Desktop running (host for the `azd` CLI image), the resource group `fleans-loadtest` (or rerun the provisioning steps in this session's transcript).

```bash
azd() { docker run --rm -v ~/.azure:/root/.azure -v "$PWD":/work -w /work azd:latest az "$@"; }

# 1. (Re)create / update each Locust test
for SCEN in linear parallel events mixed; do
  azd load test create \
    --test-id ${SCEN}-locust \
    --load-test-resource alt-fleans-loadtest \
    --resource-group fleans-loadtest \
    --test-type Locust \
    --test-plan tests/load/locust/${SCEN}.py \
    --engine-instances 2 \
    --display-name "${SCEN} Locust 500VU" \
    --env LOCUST_HOST="https://<container-app-fqdn>" \
          LOCUST_USERS=500 LOCUST_SPAWN_RATE=50 LOCUST_RUN_TIME=300
done

# 2. Run each, sequentially
for SCEN in linear parallel events mixed; do
  azd load test-run create \
    --test-id ${SCEN}-locust \
    --test-run-id ${SCEN}-1 \
    --load-test-resource alt-fleans-loadtest \
    --resource-group fleans-loadtest
done

# 3. Pull artifacts (after each run)
azd load test-run download-files \
  --test-run-id <run-id> \
  --load-test-resource alt-fleans-loadtest \
  --resource-group fleans-loadtest \
  --path tests/load/results/azure-... --result --log
```
